package im.xrl.ceobe.core.model;

/** 服务端类型（与 provider 一一对应）。 */
public enum ServerType {
    VANILLA("原版", true, false),
    PAPER("Paper（高性能插件服）", true, true),
    FABRIC("Fabric（模组服）", true, true),
    NEOFORGE("NeoForge（模组服）", true, true),
    PURPUR("Purpur（Paper 魔改）", true, true);

    private final String zhLabel;
    private final boolean supported;
    private final boolean hasModsDir;

    ServerType(String zhLabel, boolean supported, boolean hasModsDir) {
        this.zhLabel = zhLabel;
        this.supported = supported;
        this.hasModsDir = hasModsDir;
    }

    public String zhLabel() {
        return zhLabel;
    }

    /** 是否支持 mods/ 目录（Modrinth 模组安装）。 */
    public boolean hasModsDir() {
        return hasModsDir;
    }

    /** 对应的 Modrinth 加载器 id（fabric/neoforge）；无模组加载器的类型返回 null。 */
    public String loaderId() {
        return switch (this) {
            case FABRIC -> "fabric";
            case NEOFORGE -> "neoforge";
            default -> null;
        };
    }

    /** Modrinth 加载器 id → 服务端类型；不支持时抛业务异常。 */
    public static ServerType fromLoaderId(String loaderId) {
        return switch (loaderId == null ? "" : loaderId) {
            case "fabric" -> FABRIC;
            case "neoforge" -> NEOFORGE;
            default -> throw im.xrl.ceobe.core.util.CeobeException.of(
                    "暂不支持该整合包加载器: %s（当前支持 fabric / neoforge）", loaderId);
        };
    }
}
