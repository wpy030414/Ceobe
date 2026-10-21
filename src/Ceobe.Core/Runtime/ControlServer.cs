using System.Net;
using System.Net.Sockets;
using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Core.Runtime;

/// <summary>
/// 一次通道会话的可传输报告。
///
/// 为什么需要它：<c>TunnelSession</c> 是 tunnel 包内的富对象（带进程句柄），
/// 不能经控制通道传给另一个进程。整改项 1(b) 的公网侧探测结论（<see cref="Verified"/> /
/// <see cref="Diagnostic"/>）如果传不出来，探测就只是自我安慰——界面和 CLI 都看不到，
/// 用户仍然只能对着「地址已给出」猜为什么连不上。故把它压平成这个 record 过协议。
/// </summary>
/// <param name="Url">公网地址（LAN/Tailscale 为 ip:port）。</param>
/// <param name="ExpiresAt">到期时间；null = 不过期（LAN/Tailscale）。</param>
/// <param name="Verified">
/// 三态：true = 已从公网/组网侧实测可达；false = 探测未通过；null = 该通道类型不做 MC 探测
/// （Cloudflare 是 HTTP 通道，拿不到 MC status）。**false 不等于通道坏了**，措辞见 Diagnostic。
/// </param>
/// <param name="Diagnostic">可直接展示给用户的诊断文本。</param>
public sealed record TunnelReport(string? Url, DateTimeOffset? ExpiresAt, bool? Verified, string? Diagnostic)
{
    /// <summary>协议线格式：<c>url|expires|verified|diagnostic</c>。</summary>
    internal const string LineFormatHint = "url|expiresAt|verified|diagnostic";

    /// <summary>
    /// 序列化为协议行。分隔符是 <c>|</c> 且协议是行分隔的，故 Diagnostic 必须转义：
    /// 诊断文本里出现 <c>|</c> 或换行会撕碎协议帧（这是最容易踩的坑，探测文案本来就很长）。
    /// </summary>
    public string ToLine() =>
        string.Join('|',
            Url ?? "-",
            ExpiresAt?.ToString("o") ?? "-",
            Verified switch { true => "1", false => "0", _ => "-" },
            Encode(Diagnostic));

    /// <summary>反序列化协议行（容忍旧格式的 3 段与 2 段，见 <see cref="InstanceControl.TunnelStatus"/>）。</summary>
    public static TunnelReport FromLine(string line)
    {
        var parts = line.Split('|');
        return new TunnelReport(
            Url: At(parts, 0),
            ExpiresAt: ParseExpires(At(parts, 1)),
            Verified: At(parts, 2) switch { "1" => true, "0" => false, _ => null },
            Diagnostic: Decode(At(parts, 3)));
    }

    private static string? At(string[] parts, int i)
    {
        if (i >= parts.Length) return null;
        var v = parts[i];
        return v is "-" or "" ? null : v;
    }

    private static DateTimeOffset? ParseExpires(string? raw)
    {
        if (raw is null) return null;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;
    }

    private static string? Encode(string? s) =>
        string.IsNullOrEmpty(s) ? "-" : s.Replace("\\", "\\\\").Replace("|", "\\p")
            .Replace("\r", "\\r").Replace("\n", "\\n");

    private static string? Decode(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "-") return null;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            switch (s[++i])
            {
                case '\\': sb.Append('\\'); break;
                case 'p': sb.Append('|'); break;
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// 隧道能力的最小视图，由 <c>Ceobe.Core.Tunnel.TunnelManager</c> 实现。
/// ControlServer 只认这个契约，避免 runtime ↔ tunnel 互相依赖（Java 版是硬引用具体类）。
/// </summary>
public interface IInstanceTunnels
{
    /// <summary>活动通道报告（含整改项 1(b) 的探测结论）。</summary>
    IReadOnlyList<(string Kind, TunnelReport Report)> ActiveOf(string instanceName);

    /// <summary>开启通道，返回含诊断的报告。</summary>
    Task<(string Kind, TunnelReport Report)> UpAsync(string instanceName, string kindId, CancellationToken ct = default);

    void Down(string instanceName, string kindId);
}

/// <summary>
/// 实例控制通道服务端：随监督进程启动，绑定 127.0.0.1 随机端口 + 随机 token（ADR-007）。
/// 协议与 Java 版 <c>runtime/ControlServer</c> 一致，客户端见 <see cref="InstanceControl"/>。
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly ServerProcess _process;
    private readonly string _instanceDir;
    private readonly string _instanceName;
    private readonly IInstanceTunnels? _tunnels;
    private readonly TcpListener _listener;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private volatile bool _closed;

    public ControlServer(ServerProcess process, string instanceDir, string instanceName,
                         IInstanceTunnels? tunnels)
    {
        _process = process;
        _instanceDir = instanceDir;
        _instanceName = instanceName;
        _tunnels = tunnels;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        catch (Exception e) when (e is SocketException or UnauthorizedAccessException)
        {
            throw new CeobeException($"无法开启实例控制通道: {e.Message}", e);
        }
        InstanceControl.WriteCtl(instanceDir, ((IPEndPoint)_listener.LocalEndpoint).Port, _token);
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>本机控制端口（供 CLI 提示与测试）。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoop()
    {
        while (!_closed)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (SocketException)
            {
                if (!_closed) continue; // 单次 accept 失败不应终止服务
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var auth = reader.ReadLine();
                if (auth != "AUTH " + _token)
                {
                    writer.WriteLine("ERR 鉴权失败");
                    writer.WriteLine("END");
                    return;
                }
                writer.WriteLine("OK");

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    Dispatch(line, writer);
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private void Dispatch(string line, StreamWriter outWriter)
    {
        try
        {
            if (line.StartsWith("EXEC ", StringComparison.Ordinal))
            {
                _process.SendCommand(line[5..]);
                outWriter.WriteLine("OK");
            }
            else if (line == "STATE")
            {
                outWriter.WriteLine("OK");
                outWriter.WriteLine(_process.State.ToString());
            }
            else if (line.StartsWith("LOGS ", StringComparison.Ordinal))
            {
                var tail = int.Parse(line[5..].Trim());
                outWriter.WriteLine("OK");
                foreach (var l in _process.RecentLogs(tail)) outWriter.WriteLine(l);
            }
            else if (line == "STOP")
            {
                outWriter.WriteLine("OK");
                outWriter.WriteLine("END");
                outWriter.Flush();
                // 对齐 Java：先回应答，再在后台执行优雅停止
                _ = Task.Run(() => _process.Stop(TimeSpan.FromSeconds(90)));
                return;
            }
            else if (line.StartsWith("TUNNEL ", StringComparison.Ordinal))
            {
                DispatchTunnel(line[7..].Trim(), outWriter);
            }
            else if (line == "KILL")
            {
                _process.Kill();
                outWriter.WriteLine("OK");
            }
            else
            {
                outWriter.WriteLine("ERR 未知命令: " + line);
            }
        }
        catch (CeobeException e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
        catch (Exception e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
        outWriter.WriteLine("END");
    }

    /// <summary>隧道子命令：UP &lt;kind&gt; / DOWN &lt;kind&gt; / STATUS（END 由 dispatch 统一补）。</summary>
    private void DispatchTunnel(string args, StreamWriter outWriter)
    {
        if (_tunnels is null)
        {
            outWriter.WriteLine("ERR 该实例未启用隧道管理");
            return;
        }
        try
        {
            if (args == "STATUS")
            {
                outWriter.WriteLine("OK");
                foreach (var (kind, report) in _tunnels.ActiveOf(_instanceName))
                {
                    // kind 单独占一列，报告自身已是转义过的竖线格式
                    outWriter.WriteLine(kind + "|" + report.ToLine());
                }
            }
            else if (args.StartsWith("UP ", StringComparison.Ordinal))
            {
                var kindId = args[3..].Trim();
                // 控制通道是同步逐行协议，此处阻塞等待建链完成（客户端超时 150 秒）
                var (_, report) = _tunnels.UpAsync(_instanceName, kindId).GetAwaiter().GetResult();
                outWriter.WriteLine("OK");
                outWriter.WriteLine(report.Url ?? "-");
                outWriter.WriteLine(report.ExpiresAt?.ToString("o") ?? "-");
                // 第三行：探测结论。整改项 1(b) 的价值全在这一行——
                // 没有它，「地址已给出」和「地址真能连」在用户看来没区别。
                outWriter.WriteLine(report.ToLine());
            }
            else if (args.StartsWith("DOWN ", StringComparison.Ordinal))
            {
                _tunnels.Down(_instanceName, args[5..].Trim());
                outWriter.WriteLine("OK");
            }
            else
            {
                outWriter.WriteLine("ERR 未知隧道命令: " + args);
            }
        }
        catch (CeobeException e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
        catch (Exception e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        InstanceControl.DeleteCtl(_instanceDir);
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }
        return ValueTask.CompletedTask;
    }
}
