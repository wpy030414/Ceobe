using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// Minecraft「Server List Ping」协议的最小客户端（handshake + status，裸 TCP）。
/// 移植期新增（整改项 1(b)）：公网侧就绪探测，以及 LAN / Tailscale 通道的连通性自检复用。
///
/// 协议（不依赖任何具体版本，protocol id 用 -1 表示「任意」）：
/// <code>
///   C→S handshake: VarInt(len) { VarInt(0x00) VarInt(-1) String(addr) UInt16BE(port) VarInt(1) }
///   C→S status   : VarInt(1)   { VarInt(0x00) }
///   S→C status   : VarInt(len) { VarInt(jsonLen) jsonBytes(UTF-8) }
/// </code>
/// VarInt 为 LEB128 有符号编码（two's-complement，非 zigzag），最多 5 字节。
///
/// 编解码全部拆成纯函数（<see cref="WriteVarInt"/>/<see cref="ReadVarInt"/>/<see cref="BuildHandshake"/>/<see cref="ParseStatusResponse"/>），
/// 网络部分单独 <see cref="ProbeAsync"/>，便于在无网络条件下单测协议字节。
/// </summary>
public static class McPing
{
    /// <summary>探测默认超时。握手 + 一个 status 往返，公网链路留足 5 秒。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // —— VarInt（LEB128，最多 5 字节；int 的 32 位补码编码）——

    /// <summary>把 int 编码为 LEB128 VarInt（返回 1–5 字节的新数组）。</summary>
    public static byte[] WriteVarInt(int value)
    {
        Span<byte> tmp = stackalloc byte[5];
        var n = WriteVarInt(value, tmp);
        return tmp[..n].ToArray();
    }

    /// <summary>把 int 编码进 <paramref name="dest"/>，返回写入字节数（1–5）。</summary>
    public static int WriteVarInt(int value, Span<byte> dest)
    {
        // LEB128：每次取低 7 位；value 无符号右移补零，直到高位为 0。
        var uintVal = unchecked((uint)value);
        var i = 0;
        while (true)
        {
            var b = (byte)(uintVal & 0x7F);
            uintVal >>= 7;
            if (uintVal != 0)
            {
                dest[i++] = (byte)(b | 0x80);
            }
            else
            {
                dest[i++] = b;
                break;
            }
        }
        return i;
    }

    /// <summary>从字节序列 <paramref name="src"/> 的 <paramref name="offset"/> 处解码一个 VarInt。</summary>
    /// <param name="consumed">实际消费的字节数。</param>
    /// <returns>解码后的 int。</returns>
    /// <exception cref="CeobeException">超过 5 字节仍未结束（畸形数据）或越界。</exception>
    public static int ReadVarInt(ReadOnlySpan<byte> src, int offset, out int consumed)
    {
        var value = 0;
        var position = 0;
        var read = 0;
        while (true)
        {
            if (offset + read >= src.Length)
                throw CeobeException.Of("VarInt 读取越界（数据不完整）");
            var b = src[offset + read];
            read++;
            value |= (b & 0x7F) << position;
            if ((b & 0x80) == 0) break;
            position += 7;
            if (position >= 32)
                throw CeobeException.Of("VarInt 超过 5 字节（畸形数据）");
        }
        consumed = read;
        return value;
    }

    /// <summary>从流中异步读一个 VarInt（逐字节，握手/状态包都很小）。</summary>
    public static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var value = 0;
        var position = 0;
        var one = new byte[1];
        for (var i = 0; i < 5; i++)
        {
            // ReadExactlyAsync 保证读满或抛异常（EOF → EndOfStreamException）
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            var b = one[0];
            value |= (b & 0x7F) << position;
            if ((b & 0x80) == 0) return value;
            position += 7;
        }
        throw CeobeException.Of("VarInt 超过 5 字节（非 MC 响应或数据损坏）");
    }

    // —— 字符串（VarInt 长度前缀 + UTF-8）——

    /// <summary>把 <paramref name="s"/> 写成 MC String（长度前缀 + UTF-8 字节）。</summary>
    public static byte[] WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var len = WriteVarInt(bytes.Length);
        var outBytes = new byte[len.Length + bytes.Length];
        len.CopyTo(outBytes, 0);
        bytes.CopyTo(outBytes, len.Length);
        return outBytes;
    }

    // —— 报文构造 ——

    /// <summary>
    /// 构造「handshake(nextState=1) + status request」两段字节，一次发出。
    /// server-address 用隧道公网主机名，端口用隧道公网端口。
    /// </summary>
    public static byte[] BuildHandshake(string serverAddress, int port)
    {
        // packet data = VarInt(0x00) + VarInt(-1) + String(addr) + UInt16BE(port) + VarInt(1)
        using var ms = new MemoryStream();
        WriteVarIntTo(ms, 0x00);
        WriteVarIntTo(ms, -1);
        var addrBytes = WriteString(serverAddress);
        ms.Write(addrBytes, 0, addrBytes.Length);
        Span<byte> be = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(be, checked((ushort)port));
        ms.Write(be);
        WriteVarIntTo(ms, 1);
        var data = ms.ToArray();

        using var packet = new MemoryStream();
        WriteVarIntTo(packet, data.Length);
        packet.Write(data, 0, data.Length);

        // status request = VarInt(1) VarInt(0x00)
        WriteVarIntTo(packet, 1);
        WriteVarIntTo(packet, 0x00);
        return packet.ToArray();
    }

    private static void WriteVarIntTo(MemoryStream ms, int value)
    {
        Span<byte> tmp = stackalloc byte[5];
        var n = WriteVarInt(value, tmp);
        ms.Write(tmp[..n]);
    }

    // —— 响应解析 ——

    /// <summary>
    /// 从「完整 status 响应帧」解析 JSON 文本：
    /// <c>VarInt(pktLen) VarInt(packetId=0x00) VarInt(jsonLen) jsonBytes</c>。
    /// 供纯函数单测复用；<see cref="ProbeAsync"/> 网络路径逐字段读取同样先丢弃 packet id。
    /// </summary>
    /// <exception cref="CeobeException">长度字段与实际字节不符（截断/多余）。</exception>
    public static string ParseStatusResponse(ReadOnlySpan<byte> buf)
    {
        var off = 0;
        _ = ReadVarInt(buf, off, out var c1); off += c1;          // pktLen（用于完整性，下方按 jsonLen 定位）
        _ = ReadVarInt(buf, off, out var c2); off += c2;          // packet id = 0x00，丢弃
        var jsonLen = ReadVarInt(buf, off, out var c3); off += c3;
        if (jsonLen < 0 || off + jsonLen > buf.Length)
            throw CeobeException.Of("status 响应 JSON 长度与实际字节不符（可能被截断）");
        return Encoding.UTF8.GetString(buf.Slice(off, jsonLen));
    }

    // —— 网络探测 ——

    /// <summary>
    /// 向 <c>host:port</c> 发起一次 Server List Ping。返回 JSON 文本表示「拿到了合法 status 响应」。
    /// <para>这是尽力而为的自检：任何异常（连不上 / 超时 / 非 MC 响应）都归入
    /// <see cref="McPingResult.Abort"/>（「无法验证」），措辞上绝不等同于「隧道坏了」——
    /// 公网侧可能有防火墙、Pinggy 频率限制或路由延迟。</para>
    /// </summary>
    public static async Task<McPingResult> ProbeAsync(string host, int port,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var to = timeout ?? DefaultTimeout;
        byte[] request;
        try
        {
            request = BuildHandshake(host, port);
        }
        catch (OverflowException)
        {
            return McPingResult.Abort($"端口越界: {port}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(to);
        TcpClient? client = null;
        try
        {
            client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

            // 报文：VarInt(pktLen) VarInt(jsonLen) json
            var pktLen = await ReadVarIntAsync(stream, cts.Token).ConfigureAwait(false);
            if (pktLen <= 0 || pktLen > 16 * 1024 * 1024)
                return McPingResult.Abort($"status 报文长度异常: {pktLen}");

            var pkt = new byte[pktLen];
            await stream.ReadExactlyAsync(pkt, cts.Token).ConfigureAwait(false);

            // pkt 内：VarInt(0x00 报文id) VarInt(jsonLen) jsonBytes
            var off = 0;
            _ = ReadVarInt(pkt, off, out var c1); off += c1;      // 丢弃 packet id
            var jsonLen = ReadVarInt(pkt, off, out var c2); off += c2;
            if (jsonLen < 0 || off + jsonLen > pkt.Length)
                return McPingResult.Abort("status JSON 长度与实际字节不符（疑似非 MC 响应）");
            var json = Encoding.UTF8.GetString(pkt, off, jsonLen);
            return McPingResult.Success(json);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return McPingResult.Abort($"探测超时（{to.TotalSeconds:0.#} 秒）");
        }
        catch (Exception e) when (e is SocketException or IOException or CeobeException
                                  || e is OperationCanceledException)
        {
            return McPingResult.Abort(Describe(e));
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static string Describe(Exception e) => e switch
    {
        CeobeException ce => ce.Message,
        SocketException se => $"TCP 连不上（{se.SocketErrorCode}）",
        IOException => "连接被中断",
        OperationCanceledException => "探测被取消",
        _ => e.Message,
    };
}

/// <summary>
/// 一次 MC ping 的结果。刻意把「拿到合法 status JSON」与「其它一切」分开，
/// 后者统一表达为「无法验证」而非「失败」（整改项 1(b) 的措辞要求）。
/// </summary>
/// <param name="Verified">收到合法 status JSON。</param>
/// <param name="Json">成功时的原始 JSON（含 description / players / version 等，调用方可进一步校验）。</param>
/// <param name="Reason">无法验证时的诊断文本。</param>
public readonly record struct McPingResult(bool Verified, string? Json, string? Reason)
{
    /// <summary>
    /// 收到合法 status JSON。
    /// 工厂名为 Success 而非 Ok：record 的位置参数属性 Verified 若与同名静态成员并存会撞 CS0102，
    /// 故属性对齐调用方语义（<c>probe.Verified</c>），工厂另起名。
    /// </summary>
    public static McPingResult Success(string json) => new(true, json, null);

    public static McPingResult Abort(string reason) => new(false, null, reason);
}
