package im.xrl.ceobe.core.tunnel;

import java.nio.file.Path;
import java.util.function.Consumer;

/**
 * 穿透通道 SPI。
 * <p>依据 docs/DECISIONS.md ADR-003：CF Quick Tunnel 仅 HTTP，玩家 MC 裸 TCP 走 Pinggy。
 * 新增通道（playit / named tunnel）只需实现本接口并注册。
 */
public interface TunnelProvider {

    TunnelKind kind();

    /** 通道可用性（二进制/命令是否就绪）。 */
    boolean available();

    /** 不可用原因（可直接展示给用户）。 */
    String unavailableReason();

    /**
     * 启动隧道并阻塞至公网地址就绪。
     *
     * @param localPort 目标本地端口（Pinggy=MC 端口，Cloudflare=Web 控制台端口）
     * @param logSink   隧道进程诊断输出（逐行）
     */
    TunnelSession start(String instance, Path instanceDir, int localPort, Consumer<String> logSink);

    void stop(TunnelSession session);
}
