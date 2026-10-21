using System.Net.Sockets;
using System.Text;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Runtime;

/// <summary>
/// 实例控制通道客户端（实例目录下 <c>.ctl</c> 文件内容为 <c>port:token</c>）。
/// 由托管实例的「监督进程」开启，供其他 CLI 进程实现 stop / exec / state / logs，
/// 从而无需常驻全局 daemon（ADR-007）。协议与 Java 版逐字节一致：
/// 客户端 <c>AUTH &lt;token&gt;</c> → <c>OK</c>；随后每行一条命令，应答以 <c>OK</c>/<c>ERR &lt;msg&gt;</c> 起头，正文行后跟 <c>END</c>。
/// </summary>
public static class InstanceControl
{
    private const string CtlFileName = ".ctl";

    /// <summary>默认往返超时；隧道建立等长操作用 <see cref="LongTimeout"/>。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(150);

    /// <summary>控制通道是否可用（存在 .ctl 且能解析出 port:token）。</summary>
    public static bool IsAvailable(string instanceDir) => ReadCtl(instanceDir) is not null;

    /// <summary>连接被拒时清理陈旧 .ctl，避免后续误判（对齐 Java 版 catch ConnectException → clear）。</summary>
    public static void Clear(string instanceDir) => DeleteCtl(instanceDir);

    /// <summary>写 .ctl（监督进程启动控制通道后调用）。</summary>
    internal static void WriteCtl(string instanceDir, int port, string token)
    {
        try
        {
            File.WriteAllText(Path.Combine(instanceDir, CtlFileName), port + ":" + token,
                new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("写入控制通道信息失败", e);
        }
    }

    internal static void DeleteCtl(string instanceDir)
    {
        try
        {
            var f = Path.Combine(instanceDir, CtlFileName);
            if (File.Exists(f)) File.Delete(f);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static (int Port, string Token)? ReadCtl(string instanceDir)
    {
        var ctl = Path.Combine(instanceDir, CtlFileName);
        if (!File.Exists(ctl)) return null;
        try
        {
            var content = File.ReadAllText(ctl, new UTF8Encoding(false)).Trim();
            var i = content.IndexOf(':');
            if (i <= 0 || i == content.Length - 1) return null;
            // 端口必须落在合法范围，否则 TcpClient 会抛 ArgumentOutOfRangeException 而非可诊断错误
            if (!int.TryParse(content[..i], out var port) || port is < 1 or > 65535) return null;
            return (port, content[(i + 1)..]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>单条命令往返（默认超时）。</summary>
    public static List<string> Call(string instanceDir, string command) =>
        Call(instanceDir, command, DefaultTimeout);

    /// <summary>单条命令往返。</summary>
    public static List<string> Call(string instanceDir, string command, TimeSpan timeout)
    {
        var ctl = ReadCtl(instanceDir)
                  ?? throw new CeobeException("实例未由监督进程托管（无控制通道），无法执行该操作");

        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            // 对齐 Java：连接并读写均走 loopback
            client.Connect(System.Net.IPAddress.Loopback, ctl.Port);
            using var _ = client;
            client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            client.SendTimeout = (int)timeout.TotalMilliseconds;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            writer.WriteLine("AUTH " + ctl.Token);
            var auth = reader.ReadLine();
            if (auth != "OK") throw new CeobeException("控制通道鉴权失败");

            writer.WriteLine(command);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null && line != "END")
            {
                lines.Add(line);
            }
            return Parse(lines);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            Clear(instanceDir);
            throw new CeobeException("控制通道已失效（监督进程可能已退出），请重新 `ceobe start -d`");
        }
        catch (CeobeException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (IsTimeout(e))
            {
                throw CeobeException.Of(
                    "控制通道响应超时（{0:0} 秒）：该操作可能仍在后台继续", timeout.TotalSeconds);
            }
            throw new CeobeException($"控制通道连接失败（实例可能已停止）: {e.Message}", e);
        }
    }

    private static bool IsTimeout(Exception e) =>
        e is IOException && (e.InnerException is SocketException se)
        && se.SocketErrorCode == SocketError.TimedOut
        || e is System.TimeoutException
        || e is OperationCanceledException && e.InnerException is SocketException;

    /// <summary>
    /// 解析应答：剥离 <c>OK</c> / <c>OK …</c> 前缀行，遇 <c>ERR …</c> 转异常。
    /// 回归点（Java 版注释保留）：应答前缀可能无尾随空格，早期实现只剥 <c>"OK "</c>
    /// 导致状态永远读不到——此处必须同时接受 "OK" 与 "OK …"。
    /// </summary>
    internal static List<string> Parse(List<string> lines)
    {
        if (lines.Count > 0 && lines[0].StartsWith("ERR ", StringComparison.Ordinal))
        {
            throw new CeobeException(lines[0].Substring(4));
        }
        if (lines.Count > 0 && (lines[0] == "OK" || lines[0].StartsWith("OK ", StringComparison.Ordinal)))
        {
            lines.RemoveAt(0);
        }
        return lines;
    }

    public static void Stop(string instanceDir) => Call(instanceDir, "STOP");
    public static void Kill(string instanceDir) => Call(instanceDir, "KILL");
    public static void Exec(string instanceDir, string command) => Call(instanceDir, "EXEC " + command);

    public static InstanceState? State(string instanceDir)
    {
        if (!IsAvailable(instanceDir)) return null;
        var lines = Call(instanceDir, "STATE");
        if (lines.Count == 0) return null;
        return Enum.TryParse<InstanceState>(lines[0].Trim(), out var s) ? s : null;
    }

    public static List<string> Logs(string instanceDir, int tail) => Call(instanceDir, "LOGS " + tail);

    /// <summary>开启隧道，返回 [公网地址, 有效期]（第二项可能为 "-"）；建链慢，用长超时。</summary>
    public static List<string> TunnelUp(string instanceDir, string kindId) =>
        Call(instanceDir, "TUNNEL UP " + kindId, LongTimeout);

    public static void TunnelDown(string instanceDir, string kindId) =>
        Call(instanceDir, "TUNNEL DOWN " + kindId);

    /// <summary>活动通道列表，每项形如 <c>kind|url|expiresAt</c>。</summary>
    public static List<string> TunnelStatus(string instanceDir) => Call(instanceDir, "TUNNEL STATUS");
}
