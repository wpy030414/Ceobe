package im.xrl.ceobe.core.provider;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;
import im.xrl.ceobe.core.util.Versions;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * 原版服务端（Mojang piston-meta）。
 * <p>契约：manifest → 版本 JSON → {@code downloads.server.{url,sha1,size}}。
 */
public final class MojangProvider implements ServerJarProvider {

    static final String MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    @Override
    public ServerType type() {
        return ServerType.VANILLA;
    }

    @Override
    public List<String> listVersions() {
        JsonNode root = Json.parse(Http.get(MANIFEST));
        List<String> out = new ArrayList<>();
        for (JsonNode v : root.path("versions")) {
            if (!"release".equals(v.path("type").asText())) continue;
            out.add(v.path("id").asText());
        }
        out.sort(Versions.descending());
        return out;
    }

    @Override
    public InstalledServer install(Path instanceDir, String gameVersion, String loaderVersion,
                                   ProgressListener progress) {
        JsonNode entry = findVersionEntry(gameVersion);
        if (entry == null) throw CeobeException.of("Mojang 无此版本: %s", gameVersion);
        JsonNode versionJson = Json.parse(Http.get(entry.path("url").asText()));
        JsonNode server = versionJson.path("downloads").path("server");
        if (server.isMissingNode()) {
            throw CeobeException.of("版本 %s 无服务端下载（可能过旧）", gameVersion);
        }
        String url = server.path("url").asText();
        String sha1 = server.path("sha1").asText(null);
        long size = server.path("size").asLong(-1);

        progress.update("下载原版服务端 " + gameVersion, 0, size);
        Path cached = cachedJar("vanilla-" + gameVersion + ".jar", url, sha1, null, progress);
        Path target = instanceDir.resolve("server.jar");
        copy(cached, target);
        progress.update("安装完成", size, size);
        return new InstalledServer("server.jar", gameVersion, null);
    }

    private JsonNode findVersionEntry(String gameVersion) {
        JsonNode root = Json.parse(Http.get(MANIFEST));
        for (JsonNode v : root.path("versions")) {
            if (gameVersion.equals(v.path("id").asText())) return v;
        }
        return null;
    }

    /** 带 sha 去重的 jar 缓存：同名同 sha 命中则直接复用。 */
    static Path cachedJar(String key, String url, String sha1, String sha256, ProgressListener progress) {
        Path cached = CeobeHome.jarCache().resolve(key);
        if (Files.isRegularFile(cached)) {
            String actual = sha1 != null ? Http.sha1(cached) : null;
            if (sha1 == null || sha1.equalsIgnoreCase(actual)) {
                progress.update("命中缓存 " + key, 1, 1);
                return cached;
            }
        }
        Http.download(url, cached, sha1, sha256, (d, t) -> progress.update("下载 " + key, d, t));
        return cached;
    }

    static void copy(Path from, Path to) {
        try {
            Files.createDirectories(to.getParent());
            Files.copy(from, to, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        } catch (Exception e) {
            throw new CeobeException("复制到实例目录失败: " + to, e);
        }
    }
}
