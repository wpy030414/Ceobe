package im.xrl.ceobe.core.mods;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;

import java.io.IOException;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * Modrinth API v2 客户端：搜索 / 版本 / 安装（服务端侧过滤）。
 *
 * @see <a href="https://docs.modrinth.com/api/">Modrinth API 文档</a>
 */
public final class ModrinthClient {

    private static final String BASE = "https://api.modrinth.com/v2";

    /** 搜索结果条目。 */
    public record SearchHit(String projectId, String slug, String title, String description,
                            String author, long downloads, List<String> categories, String iconUrl) {
    }

    public List<SearchHit> search(String query, String gameVersion, ServerType type, int limit) {
        String loader = loaderOf(type);
        StringBuilder url = new StringBuilder(BASE + "/search?limit=" + limit
                + "&query=" + enc(query == null ? "" : query));
        List<String> facets = new ArrayList<>();
        facets.add("[[\"project_type:mod\"]]");
        if (gameVersion != null && !gameVersion.isBlank()) {
            url.append("&facets=").append(enc(
                    "[[\"project_type:mod\"],[\"versions:" + gameVersion + "\"],[\"categories:" + loader + "\"]]"));
        } else {
            url.append("&facets=").append(enc(
                    "[[\"project_type:mod\"],[\"categories:" + loader + "\"]]"));
        }
        JsonNode root = Json.parse(Http.get(url.toString()));
        List<SearchHit> hits = new ArrayList<>();
        for (JsonNode h : root.path("hits")) {
            hits.add(new SearchHit(
                    h.path("project_id").asText(),
                    h.path("slug").asText(),
                    h.path("title").asText(),
                    h.path("description").asText(),
                    h.path("author").asText(),
                    h.path("downloads").asLong(),
                    toStrings(h.path("display_categories")),
                    h.path("icon_url").asText(null)));
        }
        return hits;
    }

    /** 安装到实例 mods/，返回记录。 */
    public ModEntry install(Path instanceDir, ServerType type, String projectIdOrSlug,
                            String gameVersion, ProgressListener progress) {
        if (!type.hasModsDir()) {
            throw CeobeException.of("%s 没有 mods 目录，无法安装模组", type.zhLabel());
        }
        String loader = loaderOf(type);
        String url = BASE + "/project/" + enc(projectIdOrSlug) + "/version"
                + "?game_versions=" + enc("[\"" + gameVersion + "\"]")
                + "&loaders=" + enc("[\"" + loader + "\"]");
        JsonNode versions = Json.parse(Http.get(url));
        if (!versions.isArray() || versions.isEmpty()) {
            throw CeobeException.of("未找到适配 %s + %s 的版本", gameVersion, loader);
        }
        JsonNode version = pickPreferred(versions);
        JsonNode file = primaryFile(version);
        if (file == null) throw new CeobeException("该版本无可下载文件");

        // version.environment 是字符串枚举（如 client_only / client_or_server_prefers_both），
        // 不是 {client,server} 对象；仅 client_only 表示无法用于服务端
        String environment = version.path("environment").asText("unknown");
        if (clientOnly(environment)) {
            throw CeobeException.of("「%s」是客户端专属模组，不能装到服务端",
                    version.path("name").asText(projectIdOrSlug));
        }
        String envServer = "optional";
        String envClient = "server_only".equals(environment) ? "unsupported" : "optional";

        String fileName = file.path("filename").asText();
        String downloadUrl = file.path("url").asText();
        String sha1 = file.path("hashes").path("sha1").asText(null);
        Path modsDir = instanceDir.resolve("mods");
        CeobeHome.ensure(modsDir);
        Path target = modsDir.resolve(fileName);
        progress.update("下载模组 " + fileName, 0, file.path("size").asLong(-1));
        Http.download(downloadUrl, target, sha1, null,
                (d, t) -> progress.update("下载模组 " + fileName, d, t));

        return new ModEntry(
                version.path("project_id").asText(projectIdOrSlug),
                projectIdOrSlug,
                version.path("name").asText(projectIdOrSlug),
                version.path("version_number").asText(),
                fileName, sha1, envServer, envClient);
    }

    /** 卸载：删除记录中的文件。 */
    public void uninstall(Path instanceDir, ModEntry entry) {
        try {
            Files.deleteIfExists(instanceDir.resolve("mods").resolve(entry.fileName));
        } catch (IOException e) {
            throw new CeobeException("删除模组文件失败: " + entry.fileName, e);
        }
    }

    // —— 整合包 ——

    /** 整合包搜索结果。 */
    public record PackHit(String projectId, String slug, String title, String description,
                          String author, long downloads, List<String> categories) {
    }

    /** 整合包某版本（含 .mrpack 文件）。 */
    public record PackVersion(String projectId, String slug, String name, String versionNumber,
                              List<String> gameVersions, List<String> loaders,
                              String url, String sha1, long size) {
    }

    public List<PackHit> searchPacks(String query, String gameVersion, String loader, int limit) {
        StringBuilder facets = new StringBuilder("[[\"project_type:modpack\"]");
        if (gameVersion != null && !gameVersion.isBlank()) {
            facets.append(",[\"versions:").append(gameVersion).append("\"]");
        }
        if (loader != null && !loader.isBlank()) {
            facets.append(",[\"categories:").append(loader).append("\"]");
        }
        facets.append("]");
        String url = BASE + "/search?limit=" + limit + "&query=" + enc(query == null ? "" : query)
                + "&facets=" + enc(facets.toString());
        JsonNode root = Json.parse(Http.get(url));
        List<PackHit> hits = new ArrayList<>();
        for (JsonNode h : root.path("hits")) {
            hits.add(new PackHit(h.path("project_id").asText(), h.path("slug").asText(),
                    h.path("title").asText(), h.path("description").asText(),
                    h.path("author").asText(), h.path("downloads").asLong(),
                    toStrings(h.path("display_categories"))));
        }
        return hits;
    }

    /** 解析整合包版本（可选按 MC 版本 / 加载器过滤）。 */
    public PackVersion resolvePack(String projectIdOrSlug, String gameVersion, String loader) {
        List<String> q = new ArrayList<>();
        if (loader != null && !loader.isBlank()) q.add("loaders=" + enc("[\"" + loader + "\"]"));
        if (gameVersion != null && !gameVersion.isBlank()) {
            q.add("game_versions=" + enc("[\"" + gameVersion + "\"]"));
        }
        String url = BASE + "/project/" + enc(projectIdOrSlug) + "/version"
                + (q.isEmpty() ? "" : "?" + String.join("&", q));
        JsonNode versions = Json.parse(Http.get(url));
        if (!versions.isArray() || versions.isEmpty()) {
            throw CeobeException.of("未找到整合包 %s 的适配版本", projectIdOrSlug);
        }
        JsonNode v = pickPreferred(versions);
        JsonNode file = primaryFile(v);
        if (file == null) throw new CeobeException("该整合包版本无可下载文件");
        return new PackVersion(v.path("project_id").asText(projectIdOrSlug), projectIdOrSlug,
                v.path("name").asText(projectIdOrSlug), v.path("version_number").asText(),
                toStrings(v.path("game_versions")), toStrings(v.path("loaders")),
                file.path("url").asText(), file.path("hashes").path("sha1").asText(null),
                file.path("size").asLong(-1));
    }

    private static JsonNode pickPreferred(JsonNode versions) {
        // Modrinth 版本接口按发布时间降序返回 → 首个 release 即最新正式版
        for (JsonNode v : versions) {
            if ("release".equals(v.path("version_type").asText())) return v;
        }
        return versions.get(0);
    }

    private static JsonNode primaryFile(JsonNode version) {
        JsonNode files = version.path("files");
        for (JsonNode f : files) {
            if (f.path("primary").asBoolean(false)) return f;
        }
        return files.isEmpty() ? null : files.get(0);
    }

    private static List<String> toStrings(JsonNode arr) {
        List<String> out = new ArrayList<>();
        if (arr.isArray()) arr.forEach(n -> out.add(n.asText()));
        return out;
    }

    private static String loaderOf(ServerType type) {
        return switch (type) {
            case FABRIC -> "fabric";
            case NEOFORGE -> "neoforge";
            case PAPER, PURPUR -> "paper";
            case VANILLA -> "datapack";
        };
    }

    /** 该 {@code version.environment} 字符串是否表示客户端专属（无法用于服务端）。 */
    static boolean clientOnly(String environment) {
        return "client_only".equals(environment);
    }

    private static String enc(String s) {
        return URLEncoder.encode(s, StandardCharsets.UTF_8);
    }
}
