package im.xrl.ceobe.core.tunnel;

import im.xrl.ceobe.core.util.CeobeException;

import java.nio.file.Path;
import java.util.function.Consumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Cloudflare Quick Tunnel（免登录，仅 HTTP）。
 * <p>命令：{@code cloudflared tunnel --url http://127.0.0.1:PORT}
 * <p>地址解析：日志中的 {@code https://<slug>.trycloudflare.com}。
 * <p>实测结论：边缘节点只承载 HTTP(S)，无法传递原版 MC 裸 TCP（见 ADR-003）。
 */
public final class CloudflareQuickTunnel implements TunnelProvider {

    private static final Pattern URL =
            Pattern.compile("https://[a-z0-9-]+\\.trycloudflare\\.com");
    private static final long READY_TIMEOUT_MS = 45_000;

    @Override
    public TunnelKind kind() {
        return TunnelKind.CLOUDFLARE;
    }

    @Override
    public boolean available() {
        return TunnelProcess.which("cloudflared") != null;
    }

    @Override
    public String unavailableReason() {
        return "未找到 cloudflared，请安装后重试（https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/）";
    }

    @Override
    public TunnelSession start(String instance, Path instanceDir, int localPort, Consumer<String> logSink) {
        if (!available()) throw new CeobeException(unavailableReason());
        ProcessBuilder pb = new ProcessBuilder(
                "cloudflared", "tunnel", "--url", "http://127.0.0.1:" + localPort);
        TunnelSession session = new TunnelSession(kind(), instance, localPort, null, null);
        TunnelProcess.run(pb, instanceDir, logSink, line -> {
            Matcher m = URL.matcher(line);
            if (m.find()) session.publicUrl = m.group();
        }, () -> session.publicUrl, READY_TIMEOUT_MS, session);
        logSink.accept("[ceobe] Cloudflare 隧道就绪: " + session.publicUrl);
        return session;
    }

    @Override
    public void stop(TunnelSession session) {
        TunnelProcess.terminate(session);
    }
}
