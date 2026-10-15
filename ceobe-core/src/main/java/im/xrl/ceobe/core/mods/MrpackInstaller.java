package im.xrl.ceobe.core.mods;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;
import im.xrl.ceobe.core.util.Zips;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;

/**
 * Modrinth 整合包（`.mrpack`，实为 zip）安装器。
 * <p>读取 {@code modrinth.index.json}：按 {@code files[]} 下载（跳过 {@code env.server=unsupported}），
 * 再解包 {@code server-overrides/}（优先）与 {@code overrides/} 覆盖文件。
 */
public final class MrpackInstaller {

    /** 整合包索引摘要。 */
    public record Index(String name, String versionId, String mcVersion, String loader, String loaderVersion) {
    }

    /** 安装结果。 */
    public record Result(String name, String versionId, int files, int skipped, int overrides) {
    }

    /** 下载整合包 `.mrpack` 到缓存目录，返回本地路径（已存在则复用）。 */
    public static Path fetch(ModrinthClient.PackVersion pv, ProgressListener progress) {
        Path cache = CeobeHome.ensure(CeobeHome.root().resolve("cache").resolve("packs"));
        String fileName = pv.slug() + "-" + pv.versionNumber().replaceAll("[^A-Za-z0-9._+-]", "_") + ".mrpack";
        Path target = cache.resolve(fileName);
        if (!Files.isRegularFile(target)) {
            Http.download(pv.url(), target, pv.sha1(), null,
                    (d, t) -> progress.update("下载整合包 " + fileName, d, t));
        }
        return target;
    }

    /** 解析 {@code modrinth.index.json}（仅读头部信息，不解包）。 */
    public static Index readIndex(Path mrpack) {
        Path tmp = CeobeHome.ensure(
                mrpack.getParent().resolve(mrpack.getFileName() + ".index"));
        try {
            Zips.deleteRecursively(tmp);
            // 只取索引文件：解压全部再读亦可，但此处只需 index
            Zips.extract(mrpack, tmp);
            Path index = tmp.resolve("modrinth.index.json");
            if (!Files.isRegularFile(index)) {
                throw new CeobeException("不是有效的 .mrpack：缺少 modrinth.index.json");
            }
            JsonNode root = Json.parse(Files.readString(index, StandardCharsets.UTF_8));
            JsonNode deps = root.path("dependencies");
            String loader = null;
            String loaderVersion = null;
            for (Map.Entry<String, String> e : Map.of(
                    "fabric-loader", "fabric", "neoforge", "neoforge",
                    "forge", "forge", "quilt-loader", "quilt").entrySet()) {
                if (deps.has(e.getKey())) {
                    loader = e.getValue();
                    loaderVersion = deps.path(e.getKey()).asText(null);
                    break;
                }
            }
            return new Index(
                    root.path("name").asText(""),
                    root.path("versionId").asText(""),
                    deps.path("minecraft").asText(null),
                    loader, loaderVersion);
        } catch (IOException e) {
            throw new CeobeException("读取整合包索引失败: " + e.getMessage(), e);
        } finally {
            Zips.deleteRecursively(tmp);
        }
    }

    /** 把整合包安装进已有实例目录。 */
    public static Result install(Path instanceDir, Path mrpack, ProgressListener progress) {
        Index index = readIndex(mrpack);

        Path work = CeobeHome.ensure(instanceDir.resolve(".mrpack-work"));
        Zips.deleteRecursively(work);
        try {
            Zips.extract(mrpack, work);
            JsonNode root = Json.parse(Files.readString(
                    work.resolve("modrinth.index.json"), StandardCharsets.UTF_8));

            int files = 0;
            int skipped = 0;
            for (JsonNode f : root.path("files")) {
                String envServer = f.path("env").path("server").asText("required");
                String path = f.path("path").asText("");
                String url = firstDownload(f);
                if ("unsupported".equals(envServer) || path.isBlank() || url == null) {
                    skipped++;
                    continue;
                }
                Path target = instanceDir.resolve(path).normalize();
                if (!target.startsWith(instanceDir)) {   // 防穿越
                    skipped++;
                    continue;
                }
                Files.createDirectories(target.getParent());
                String sha1 = f.path("hashes").path("sha1").asText(null);
                progress.update("下载整合包文件 " + target.getFileName(), 0, -1);
                Http.download(url, target, sha1, null, (d, t) -> {
                });
                files++;
            }

            // server-overrides 优先于 overrides
            int overrides = Zips.copyInto(work.resolve("server-overrides"), instanceDir)
                    + Zips.copyInto(work.resolve("overrides"), instanceDir);

            return new Result(index.name(), index.versionId(), files, skipped, overrides);
        } catch (IOException e) {
            throw new CeobeException("安装整合包失败: " + e.getMessage(), e);
        } finally {
            Zips.deleteRecursively(work);
        }
    }

    private static String firstDownload(JsonNode file) {
        JsonNode downloads = file.path("downloads");
        return downloads.isArray() && !downloads.isEmpty() ? downloads.get(0).asText(null) : null;
    }

    private MrpackInstaller() {
    }
}
