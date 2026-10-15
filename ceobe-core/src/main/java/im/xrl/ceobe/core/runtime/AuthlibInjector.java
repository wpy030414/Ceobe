package im.xrl.ceobe.core.runtime;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;

import java.nio.file.Files;
import java.nio.file.Path;

/**
 * authlib-injector：以 {@code -javaagent} 方式注入，实现 Yggdrasil（三方鉴权）对接。
 *
 * @see <a href="https://authlib-injector.yushi.moe/">authlib-injector</a>
 */
public final class AuthlibInjector {

    private static final String LATEST = "https://authlib-injector.yushi.moe/artifact/latest.json";

    /** 确保 authlib-injector.jar 已在缓存中，返回其路径。 */
    public static Path ensure(ProgressListener progress) {
        Path cached = CeobeHome.ensure(CeobeHome.root().resolve("cache").resolve("jars"))
                .resolve("authlib-injector.jar");
        if (Files.isRegularFile(cached)) return cached;
        JsonNode root = Json.parse(Http.get(LATEST));
        String url = root.path("download_url").asText(null);
        if (url == null || url.isBlank()) {
            throw new CeobeException("无法解析 authlib-injector 下载地址");
        }
        String sha256 = root.path("checksums").path("sha256").asText(null);
        progress.update("下载 authlib-injector", 0, -1);
        Http.download(url, cached, null, sha256, (d, t) -> {
        });
        return cached;
    }

    private AuthlibInjector() {
    }
}
