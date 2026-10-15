package im.xrl.ceobe.core.jdk;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;

import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashSet;
import java.util.Set;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * 按平台一键安装 Temurin JDK（Eclipse Adoptium）。
 * <p>优先清华镜像（`mirrors.tuna.tsinghua.edu.cn/Adoptium/...`），失败回退 Adoptium 官方（GitHub）。
 * <p>产物落在 {@code ~/.ceobe/jdk/<jdk-…>/}。
 */
public final class JdkInstaller {

    private static final String ASSETS =
            "https://api.adoptium.net/v3/assets/latest/%d/hotspot?os=%s&architecture=%s&image_type=jdk";
    private static final String TUNA = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/%d/jdk/%s/%s/%s";

    public static Path install(int major, ProgressListener progress) {
        String os = os();
        String arch = arch();

        JsonNode arr = Json.parse(Http.get(ASSETS.formatted(major, os, arch)));
        if (!arr.isArray() || arr.isEmpty()) {
            throw CeobeException.of("Adoptium 无 Java %d 的 %s/%s 构建", major, os, arch);
        }
        JsonNode pkg = arr.get(0).path("binary").path("package");
        String name = pkg.path("name").asText();
        String gh = pkg.path("link").asText();

        Path cache = CeobeHome.ensure(CeobeHome.root().resolve("cache").resolve("jdk"));
        Path archive = cache.resolve(name);
        if (!Files.isRegularFile(archive)) {
            progress.update("下载 JDK " + major + "（" + os + "/" + arch + "）", 0, -1);
            String tuna = TUNA.formatted(major, arch, os, name);
            try {
                Http.download(tuna, archive, null, null,
                        (d, t) -> progress.update("下载 JDK " + major, d, t));
            } catch (RuntimeException mirrorFailed) {
                progress.update("清华镜像不可用，改用 Adoptium 官方源", 0, -1);
                Http.download(gh, archive, null, null,
                        (d, t) -> progress.update("下载 JDK " + major, d, t));
            }
        }

        progress.update("解压 JDK", 0, -1);
        Path home = extract(archive);
        progress.update("JDK 就绪: " + home, 1, 1);
        return home;
    }

    /** 解压并把新出现的、含 bin/java 的目录作为 javaHome。 */
    private static Path extract(Path archive) {
        Path root = JdkStore.root();
        Set<String> before = childNames(root);
        String fn = archive.getFileName().toString().toLowerCase();
        try {
            if (fn.endsWith(".zip")) {
                unzip(archive, root);
            } else if (fn.endsWith(".tar.gz") || fn.endsWith(".tgz")) {
                untar(archive, root);
            } else {
                throw new CeobeException("不支持的 JDK 归档格式: " + fn);
            }
        } catch (IOException e) {
            throw new CeobeException("解压 JDK 失败: " + e.getMessage(), e);
        }
        try (var stream = Files.list(root)) {
            return stream.filter(Files::isDirectory)
                    .filter(d -> !before.contains(d.getFileName().toString()))
                    .filter(d -> Files.isRegularFile(d.resolve("bin").resolve(JdkStore.javaExe())))
                    .findFirst()
                    .orElseThrow(() -> new CeobeException("解压后未找到 JDK 目录（bin/java 缺失）"));
        } catch (IOException e) {
            throw new CeobeException("定位 JDK 目录失败", e);
        }
    }

    private static Set<String> childNames(Path dir) {
        Set<String> names = new HashSet<>();
        try (var stream = Files.list(dir)) {
            stream.forEach(p -> names.add(p.getFileName().toString()));
        } catch (IOException ignored) {
        }
        return names;
    }

    private static void unzip(Path archive, Path dest) throws IOException {
        try (ZipInputStream in = new ZipInputStream(Files.newInputStream(archive))) {
            ZipEntry entry;
            while ((entry = in.getNextEntry()) != null) {
                Path target = dest.resolve(entry.getName()).normalize();
                if (!target.startsWith(dest)) continue;   // 防目录穿越
                if (entry.isDirectory()) {
                    Files.createDirectories(target);
                } else {
                    Files.createDirectories(target.getParent());
                    Files.copy(in, target, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                }
            }
        }
    }

    private static void untar(Path archive, Path dest) {
        try {
            Process p = new ProcessBuilder("tar", "-xzf", archive.toString(), "-C", dest.toString())
                    .redirectErrorStream(true).start();
            String out = new String(readAll(p.getInputStream()));
            int code = p.waitFor();
            if (code != 0) {
                throw new CeobeException("tar 解压失败（退出码 " + code + "）: " + out.trim());
            }
        } catch (IOException e) {
            throw new CeobeException("无法调用 tar 解压（Linux/macOS 需系统 tar）: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new CeobeException("tar 解压被中断", e);
        }
    }

    private static byte[] readAll(InputStream in) throws IOException {
        return in.readAllBytes();
    }

    static String os() {
        String n = System.getProperty("os.name", "").toLowerCase();
        if (n.contains("win")) return "windows";
        if (n.contains("mac") || n.contains("darwin")) return "mac";
        return "linux";
    }

    static String arch() {
        String a = System.getProperty("os.arch", "").toLowerCase();
        if (a.equals("aarch64") || a.equals("arm64")) return "aarch64";
        return "x64";
    }

    private JdkInstaller() {
    }
}
