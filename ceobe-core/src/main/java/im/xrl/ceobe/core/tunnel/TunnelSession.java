package im.xrl.ceobe.core.tunnel;

import java.time.Instant;

/** 一次穿透会话。 */
public final class TunnelSession {
    public final TunnelKind kind;
    public final String instance;
    public final int localPort;
    public volatile String publicUrl;
    public final Instant startedAt = Instant.now();
    /** 到期时间（null = 不过期）。 */
    public volatile Instant expiresAt;
    volatile Process process;

    public TunnelSession(TunnelKind kind, String instance, int localPort, String publicUrl,
                         Instant expiresAt) {
        this.kind = kind;
        this.instance = instance;
        this.localPort = localPort;
        this.publicUrl = publicUrl;
        this.expiresAt = expiresAt;
    }

    public boolean isExpiringSoon(long withinSeconds) {
        return expiresAt != null && Instant.now().plusSeconds(withinSeconds).isAfter(expiresAt);
    }
}
