using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// Cloudflare Quick Tunnel（免登录，仅 HTTP）。移植自 Java <c>tunnel/CloudflareQuickTunnel.java</c>。
/// <para>命令：<c>cloudflared tunnel --url http://localhost:&lt;webPort&gt;</c></para>
/// <para>地址解析：日志中的 <c>https://&lt;slug&gt;.trycloudflare.com</c>。</para>
/// <para>实测结论（ADR-003）：边缘节点只承载 HTTP(S)，无法传递原版 MC 裸 TCP；
/// 本通道指向 **Web 控制台端口**而非 MC 端口。MC ping 探测不适用于 HTTP 通道，故不做。</para>
/// </summary>
public sealed class CloudflareQuickTunnel : ITunnelProvider
{
    /// <summary>trycloudflare 地址正则（与 Java 逐字符一致）。</summary>
    internal static readonly System.Text.RegularExpressions.Regex UrlPattern =
        new(@"https://[a-z0-9-]+\.trycloudflare\.com", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);

    public TunnelKind Kind => TunnelKind.CLOUDFLARE;

    public bool Available => TunnelProcess.Which("cloudflared") is not null;

    public string UnavailableReason =>
        "未找到 cloudflared，请安装后重试（https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/）";

    public async Task<TunnelSession> StartAsync(string instance, string instanceDir, int localPort,
        Action<string> logSink, CancellationToken ct = default)
    {
        if (!Available) throw new CeobeException(UnavailableReason);
        // 硬约束 3：指向 Web 控制台端口，仅 HTTP；MC ping 探测不适用，不做。
        var psi = PinggyTunnel.NewStartInfo(new[]
        {
            "cloudflared", "tunnel", "--url", "http://localhost:" + localPort,
        });
        var session = new TunnelSession(Kind, instance, localPort, null, null);
        await TunnelProcess.RunAsync(psi, instanceDir, logSink, line =>
        {
            var m = UrlPattern.Match(line);
            if (m.Success) session.PublicUrl = m.Value;
        }, () => session.PublicUrl, ReadyTimeout, session, ct).ConfigureAwait(false);

        session.Verified = null; // HTTP 通道不做 MC 探测：未验证 ≠ 失败
        session.Diagnostic = null;
        logSink("[ceobe] Cloudflare 隧道就绪: " + session.PublicUrl);
        return session;
    }

    public void Stop(TunnelSession session) => TunnelProcess.Terminate(session);
}
