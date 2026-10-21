using System.Diagnostics;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// Pinggy 免登录裸 TCP 隧道（承载玩家 MC 连接）。移植自 Java <c>tunnel/PinggyTunnel.java</c>。
/// <para>命令（硬约束 1，逐参数照搬，<c>-4</c> 是实测结论不得删）：
/// <c>ssh -4 -p 443 -R0:127.0.0.1:&lt;port&gt; -o StrictHostKeyChecking=accept-new
/// -o UserKnownHostsFile=&lt;instanceDir&gt;/pinggy_known_hosts -o ServerAliveInterval=30
/// -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes -T tcp@a.pinggy.io</c></para>
/// <para>地址解析：stdout 中的 <c>tcp://&lt;host&gt;:&lt;port&gt;</c>；就绪超时 45 秒。</para>
/// <para>会话 55 分钟（免费通道 60 分钟留 5 分钟余量）→ <see cref="TunnelManager"/> 到期前自动续期。</para>
/// </summary>
public sealed class PinggyTunnel : ITunnelProvider
{
    /// <summary>公网地址正则（硬约束 1，与 Java 逐字符一致）。</summary>
    internal static readonly System.Text.RegularExpressions.Regex TcpUrl =
        new(@"tcp://([a-z0-9.\-]+):(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>就绪超时（45 秒，与 Java 一致）。</summary>
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);

    /// <summary>免费通道 60 分钟，留 5 分钟余量 → 会话 55 分钟。</summary>
    internal static readonly TimeSpan Session = TimeSpan.FromMinutes(55);

    public TunnelKind Kind => TunnelKind.PINGGY;

    public bool Available => TunnelProcess.Which("ssh") is not null;

    public string UnavailableReason => "未找到 ssh 客户端（Windows 可安装「OpenSSH 客户端」可选功能）";

    public async Task<TunnelSession> StartAsync(string instance, string instanceDir, int localPort,
        Action<string> logSink, CancellationToken ct = default)
    {
        if (!Available) throw new CeobeException(UnavailableReason);

        // 整改项 1(a)：绑定一致性——隧道打到 127.0.0.1，server-ip 若绑了具体网卡地址则必死，先拦截。
        TunnelBindGuard.AssertCompatible(instanceDir, "127.0.0.1", localPort, "Pinggy 玩家通道");

        var knownHosts = Path.Combine(instanceDir, "pinggy_known_hosts");
        // -4 强制 IPv4：部分网络到 pinggy.io 的 IPv6 路径会被 Connection reset（实测结论，不得删）
        var psi = NewStartInfo(new[]
        {
            "ssh", "-4", "-p", "443",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "UserKnownHostsFile=" + Path.GetFullPath(knownHosts),
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=3",
            "-o", "ExitOnForwardFailure=yes",
            "-T",
            "-R0:127.0.0.1:" + localPort,
            "tcp@a.pinggy.io",
        });

        var session = new TunnelSession(Kind, instance, localPort, null, DateTimeOffset.UtcNow.Add(Session));
        await TunnelProcess.RunAsync(psi, instanceDir, logSink, line =>
        {
            var m = TcpUrl.Match(line);
            if (m.Success) session.PublicUrl = m.Value;
        }, () => session.PublicUrl, ReadyTimeout, session, ct).ConfigureAwait(false);

        // 整改项 1(b)：公网侧就绪探测——解析到 URL ≠ 通道能打到 MC。
        var (host, port) = SplitTcp(session.PublicUrl!);
        var probe = await McPing.ProbeAsync(host, port, McPing.DefaultTimeout, ct).ConfigureAwait(false);
        session.Verified = probe.Verified;
        session.Diagnostic = probe.Verified
            ? $"已验证：从公网侧 {probe.Json?[..Math.Min(120, probe.Json.Length)]}…"
            : $"无法验证（不代表通道坏了，可能是公网防火墙/频率限制）：{probe.Reason}";
        logSink($"[ceobe] Pinggy 公网侧探测：{session.Diagnostic}");
        logSink($"[ceobe] Pinggy 通道就绪: {session.PublicUrl}（约 55 分钟后自动续期）");
        return session;
    }

    public void Stop(TunnelSession session) => TunnelProcess.Terminate(session);

    /// <summary>供 TunnelManager 续期与守卫复用：同参数的 ProcessStartInfo 工厂（stdout/stderr 重定向）。</summary>
    internal static ProcessStartInfo NewStartInfo(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < args.Count; i++) psi.ArgumentList.Add(args[i]);
        return psi;
    }

    /// <summary>tcp://host:port → (host, port)；解析失败抛异常（正常路径不会到这里）。</summary>
    internal static (string Host, int Port) SplitTcp(string tcpUrl)
    {
        var m = TcpUrl.Match(tcpUrl);
        if (!m.Success) throw CeobeException.Of("无法解析隧道地址: {0}", tcpUrl);
        return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
    }
}
