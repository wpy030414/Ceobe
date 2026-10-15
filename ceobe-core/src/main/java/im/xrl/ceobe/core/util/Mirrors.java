package im.xrl.ceobe.core.util;

import java.net.URI;

/**
 * 下载镜像映射：把上游 URL 改写到国内镜像站，显著加速（见 AGENTS「下载一律优先镜像」）。
 *
 * <p>已实测覆盖：
 * <ul>
 *   <li>BMCLAPI（`bmclapi2.bangbang93.com`）：Mojang piston-meta/piston-data（同路径换主机）、Fabric meta（`/fabric-meta` 前缀）。</li>
 *   <li>mcimirror（`mod.mcimirror.top`）：Modrinth CDN（同路径换主机）。</li>
 * </ul>
 * 无镜像的源（PaperMC Fill、Purpur API、maven.neoforged.net、maven.fabricmc.net、api.modrinth.com）保持直连。
 *
 * <p>可用 `-Dceobe.mirror=off` 关闭镜像，或 `-Dceobe.mirror.bmclapi=` / `-Dceobe.mirror.modrinth=` 覆盖镜像基址。
 */
public final class Mirrors {

    private static final String BMCLAPI =
            System.getProperty("ceobe.mirror.bmclapi", "https://bmclapi2.bangbang93.com");
    private static final String MODRINTH =
            System.getProperty("ceobe.mirror.modrinth", "https://mod.mcimirror.top");
    private static final boolean ENABLED =
            !"off".equalsIgnoreCase(System.getProperty("ceobe.mirror", "on"));

    /** 返回该 URL 的镜像地址；无镜像或已禁用时返回 null（调用方回退上游）。 */
    public static String forUrl(String url) {
        if (!ENABLED || url == null || url.isBlank()) return null;
        try {
            URI u = URI.create(url);
            String host = u.getHost() == null ? "" : u.getHost().toLowerCase();
            String path = u.getRawPath() == null ? "" : u.getRawPath();
            String suffix = u.getRawQuery() != null ? "?" + u.getRawQuery() : "";
            return switch (host) {
                case "piston-meta.mojang.com", "piston-data.mojang.com", "launcher.mojang.com" ->
                        BMCLAPI + path + suffix;
                case "meta.fabricmc.net" -> BMCLAPI + "/fabric-meta" + path + suffix;
                case "cdn.modrinth.com" -> MODRINTH + path + suffix;
                default -> null;
            };
        } catch (RuntimeException e) {
            return null;
        }
    }

    public static boolean enabled() {
        return ENABLED;
    }

    private Mirrors() {
    }
}
