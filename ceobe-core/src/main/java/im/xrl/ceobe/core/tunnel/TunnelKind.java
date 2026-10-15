package im.xrl.ceobe.core.tunnel;

/** 穿透通道类型。 */
public enum TunnelKind {
    /** Pinggy（免登录、裸 TCP、60 分钟会话自动续期）——承载玩家 MC 连接。 */
    PINGGY("pinggy", "玩家连接（Pinggy TCP）"),
    /** Cloudflare Quick Tunnel（免登录、仅 HTTP）——承载 Web 控制台。 */
    CLOUDFLARE("cloudflare", "Web 控制台（Cloudflare）"),
    /** 局域网直连（无第三方依赖，仅同一网络内可达）。 */
    LAN("lan", "局域网直连");

    private final String id;
    private final String zhLabel;

    TunnelKind(String id, String zhLabel) {
        this.id = id;
        this.zhLabel = zhLabel;
    }

    public String id() {
        return id;
    }

    public String zhLabel() {
        return zhLabel;
    }

    public static TunnelKind fromId(String id) {
        for (TunnelKind k : values()) {
            if (k.id.equalsIgnoreCase(id)) return k;
        }
        throw new IllegalArgumentException("未知通道: " + id);
    }
}
