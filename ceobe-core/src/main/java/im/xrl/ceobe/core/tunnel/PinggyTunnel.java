package im.xrl.ceobe.core.tunnel;

import im.xrl.ceobe.core.util.CeobeException;

import java.nio.file.Path;
import java.time.Duration;
import java.time.Instant;
import java.util.function.Consumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Pinggy 免登录裸 TCP 隧道（承载玩家 MC 连接）。
 * <p>命令：{@code ssh -p 443 -R0:127.0.0.1:MC_PORT tcp@a.pinggy.io}
 * <p>地址解析：stdout 中的 {@code tcp://<host>:<port>}。
 * <p>会话 60 分钟 → {@link im.xrl.ceobe.core.tunnel.TunnelManager} 到期前自动续期。
 */
public final class PinggyTunnel implements TunnelProvider {

    private static final Pattern TCP_URL = Pattern.compile("tcp://([a-z0-9.\\-]+):(\\d+)");
    private static final long READY_TIMEOUT_MS = 45_000;
    /** 免费通道 60 分钟，留 5 分钟余量。 */
    static final Duration SESSION = Duration.ofMinutes(55);

    @Override
    public TunnelKind kind() {
        return TunnelKind.PINGGY;
    }

    @Override
    public boolean available() {
        return TunnelProcess.which("ssh") != null;
    }

    @Override
    public String unavailableReason() {
        return "未找到 ssh 客户端（Windows 可安装「OpenSSH 客户端」可选功能）";
    }

    @Override
    public TunnelSession start(String instance, Path instanceDir, int localPort, Consumer<String> logSink) {
        if (!available()) throw new CeobeException(unavailableReason());
        Path knownHosts = instanceDir.resolve("pinggy_known_hosts");
        // -4 强制 IPv4：部分网络到 pinggy.io 的 IPv6 路径会被 Connection reset（实测）
        ProcessBuilder pb = new ProcessBuilder(
                "ssh", "-4", "-p", "443",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "UserKnownHostsFile=" + knownHosts.toAbsolutePath(),
                "-o", "ServerAliveInterval=30",
                "-o", "ServerAliveCountMax=3",
                "-o", "ExitOnForwardFailure=yes",
                "-T",
                "-R0:127.0.0.1:" + localPort,
                "tcp@a.pinggy.io");
        TunnelSession session = new TunnelSession(kind(), instance, localPort, null,
                Instant.now().plus(SESSION));
        TunnelProcess.run(pb, instanceDir, logSink, line -> {
            Matcher m = TCP_URL.matcher(line);
            if (m.find()) session.publicUrl = m.group();
        }, () -> session.publicUrl, READY_TIMEOUT_MS, session);
        logSink.accept("[ceobe] Pinggy 通道就绪: " + session.publicUrl
                + "（约 55 分钟后自动续期）");
        return session;
    }

    @Override
    public void stop(TunnelSession session) {
        TunnelProcess.terminate(session);
    }
}
