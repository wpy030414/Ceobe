package im.xrl.ceobe.core.provider;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import im.xrl.ceobe.core.util.Json;
import im.xrl.ceobe.core.util.Versions;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

/**
 * Fabric（meta v2 + installer 子进程）。
 * <p>安装方式：{@code java -jar fabric-installer.jar server -dir <d> -mcversion <v> -loader <l> -downloadMinecraft}；
 * 产物为 {@code fabric-server-launch.jar}（+ libraries/）。
 */
public final class FabricProvider implements ServerJarProvider {

    private static final String META = "https://meta.fabricmc.net/v2/versions";

    @Override
    public ServerType type() {
        return ServerType.FABRIC;
    }

    @Override
    public List<String> listVersions() {
        JsonNode root = Json.parse(Http.get(META + "/game"));
        List<String> out = new ArrayList<>();
        for (JsonNode v : root) {
            if (v.path("stable").asBoolean() && Versions.isRelease(v.path("version").asText())) {
                out.add(v.path("version").asText());
            }
        }
        out.sort(Versions.descending());
        return out;
    }

    @Override
    public List<String> listLoaderVersions(String gameVersion) {
        JsonNode root = Json.parse(Http.get(META + "/loader/" + gameVersion));
        List<String> out = new ArrayList<>();
        for (JsonNode v : root) {
            if (v.path("loader").path("stable").asBoolean()) {
                out.add(v.path("loader").path("version").asText());
            }
        }
        return out.isEmpty() ? latestLoaders() : out;
    }

    private List<String> latestLoaders() {
        JsonNode root = Json.parse(Http.get(META + "/loader"));
        List<String> out = new ArrayList<>();
        for (JsonNode v : root) {
            if (v.path("stable").asBoolean()) out.add(v.path("version").asText());
        }
        out.sort(Comparator.reverseOrder());
        return out;
    }

    @Override
    public InstalledServer install(Path instanceDir, String gameVersion, String loaderVersion,
                                   ProgressListener progress) {
        String loader = (loaderVersion == null || loaderVersion.isBlank())
                ? latestLoaders().stream().findFirst()
                        .orElseThrow(() -> new CeobeException("Fabric 无可用 stable loader"))
                : loaderVersion;

        String installerUrl = Json.parse(Http.get(META + "/installer")).get(0).path("url").asText();
        progress.update("下载 Fabric 安装器", 0, -1);
        Path installer = MojangProvider.cachedJar("fabric-installer.jar", installerUrl, null, null, progress);

        progress.update("运行 Fabric 安装器（含下载原版服务端，可能较慢）", 0, -1);
        List<String> cmd = new ArrayList<>(List.of(
                javaBinary(), "-jar", installer.toString(), "server",
                "-dir", instanceDir.toAbsolutePath().toString(),
                "-mcversion", gameVersion,
                "-loader", loader,
                "-downloadMinecraft"));
        runAndCapture(cmd, instanceDir);

        Path launch = instanceDir.resolve("fabric-server-launch.jar");
        if (!Files.isRegularFile(launch)) {
            throw new CeobeException("Fabric 安装未产出 fabric-server-launch.jar，请检查安装器输出");
        }
        progress.update("安装完成", 1, 1);
        return new InstalledServer("fabric-server-launch.jar", gameVersion, loader);
    }

    private static String javaBinary() {
        Path java = Path.of(System.getProperty("java.home"), "bin", "java");
        return Files.isRegularFile(java) ? java.toString() : "java";
    }

    private static void runAndCapture(List<String> cmd, Path workDir) {
        try {
            ProcessBuilder pb = new ProcessBuilder(cmd).directory(workDir.toFile());
            pb.redirectErrorStream(true);
            Process p = pb.start();
            StringBuilder out = new StringBuilder();
            try (var reader = new java.io.BufferedReader(
                    new java.io.InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = reader.readLine()) != null) {
                    out.append(line).append('\n');
                    if (out.length() > 20_000) out.delete(0, out.length() - 20_000);
                }
            }
            int code = p.waitFor();
            if (code != 0) {
                throw CeobeException.of("Fabric 安装器退出码 %d\n%s", code, tail(out.toString()));
            }
        } catch (IOException e) {
            throw new CeobeException("无法启动 Fabric 安装器: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new CeobeException("Fabric 安装被中断", e);
        }
    }

    private static String tail(String text) {
        String[] lines = text.split("\n");
        int from = Math.max(0, lines.length - 12);
        return String.join("\n", java.util.Arrays.copyOfRange(lines, from, lines.length));
    }
}
