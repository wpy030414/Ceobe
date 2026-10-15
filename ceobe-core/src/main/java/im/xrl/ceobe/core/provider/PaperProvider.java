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
 * PaperMC Fill v3（paper / folia）。
 * <p>契约：{@code /v3/projects/{p}/versions/{v}/builds/latest} →
 * {@code downloads["server:default"].{url,checksums.sha256}}。
 * <p>注意：旧的 {@code api.papermc.io/v2} 已 410 Gone，不得回退使用。
 */
public final class PaperProvider implements ServerJarProvider {

    private static final String BASE = "https://fill.papermc.io/v3/projects";
    private final String project;
    private final ServerType type;

    public PaperProvider() {
        this("paper", ServerType.PAPER);
    }

    public PaperProvider(String project, ServerType type) {
        this.project = project;
        this.type = type;
    }

    @Override
    public ServerType type() {
        return type;
    }

    @Override
    public List<String> listVersions() {
        JsonNode root = Json.parse(Http.get(BASE + "/" + project));
        List<String> out = new ArrayList<>();
        JsonNode versions = root.path("versions");
        versions.properties().forEach(e -> {
            for (JsonNode v : e.getValue()) {
                String id = v.asText();
                if (Versions.isRelease(id)) out.add(id);
            }
        });
        out.sort(Versions.descending());
        return out;
    }

    @Override
    public InstalledServer install(Path instanceDir, String gameVersion, String loaderVersion,
                                   ProgressListener progress) {
        JsonNode build;
        try {
            build = Json.parse(Http.get(BASE + "/" + project + "/versions/" + gameVersion + "/builds/latest"));
        } catch (CeobeException e) {
            throw new CeobeException(
                    "%s 暂不支持版本 %s（可换版本或改用其他服务端类型）".formatted(project, gameVersion), e);
        }
        JsonNode download = build.path("downloads").path("server:default");
        if (download.isMissingNode()) {
            throw new CeobeException(project + " 未返回服务端下载信息，可能无该版本构建");
        }
        String url = download.path("url").asText();
        String sha256 = download.path("checksums").path("sha256").asText(null);
        long size = download.path("size").asLong(-1);

        progress.update("下载 " + project + " " + gameVersion, 0, size);
        Path cached = MojangProvider.cachedJar(
                "%s-%s.jar".formatted(project, gameVersion), url, null, sha256, progress);
        MojangProvider.copy(cached, instanceDir.resolve("server.jar"));
        progress.update("安装完成", size, size);
        return new InstalledServer("server.jar", gameVersion, null);
    }
}
