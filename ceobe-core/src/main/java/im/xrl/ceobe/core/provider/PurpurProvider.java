package im.xrl.ceobe.core.provider;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;
import im.xrl.ceobe.core.util.Versions;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * Purpur 官方 API。
 * <p>契约：{@code /v2/purpur} 版本列表 → {@code /v2/purpur/{v}} builds.latest
 * → {@code /v2/purpur/{v}/{build}/download}。
 */
public final class PurpurProvider implements ServerJarProvider {

    private static final String BASE = "https://api.purpurmc.org/v2/purpur";

    @Override
    public ServerType type() {
        return ServerType.PURPUR;
    }

    @Override
    public List<String> listVersions() {
        JsonNode root = Json.parse(Http.get(BASE));
        List<String> out = new ArrayList<>();
        for (JsonNode v : root.path("versions")) {
            String id = v.asText();
            if (Versions.isRelease(id)) out.add(id);
        }
        out.sort(Versions.descending());
        return out;
    }

    @Override
    public InstalledServer install(Path instanceDir, String gameVersion, String loaderVersion,
                                   ProgressListener progress) {
        JsonNode info;
        try {
            info = Json.parse(Http.get(BASE + "/" + gameVersion));
        } catch (CeobeException e) {
            throw CeobeException.of("Purpur 无版本 %s", gameVersion);
        }
        String build = info.path("builds").path("latest").asText(null);
        if (build == null || build.isBlank()) {
            throw CeobeException.of("Purpur %s 尚无构建", gameVersion);
        }
        String url = "%s/%s/%s/download".formatted(BASE, gameVersion, build);
        progress.update("下载 Purpur " + gameVersion + " build " + build, 0, -1);
        Path cached = MojangProvider.cachedJar(
                "purpur-%s-%s.jar".formatted(gameVersion, build), url, null, null, progress);
        MojangProvider.copy(cached, instanceDir.resolve("server.jar"));
        progress.update("安装完成", 1, 1);
        return new InstalledServer("server.jar", gameVersion, null);
    }
}
