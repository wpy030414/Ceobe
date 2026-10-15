package im.xrl.ceobe.core.model;

/** 服务端类型（与 provider 一一对应）。 */
public enum ServerType {
    VANILLA("原版", true, false),
    PAPER("Paper（高性能插件服）", true, true),
    FABRIC("Fabric（模组服）", true, true),
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
}
