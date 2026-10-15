package im.xrl.ceobe.core.provider;

import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * NeoForge（1.20.2+ 的 Forge 分叉，maven.neoforged.net）。
 *
 * <p>安装：下载 {@code neoforge-<ver>-installer.jar} 并运行 {@code --installServer <dir>}；
 * 产物无单一启动 jar，改用参数文件启动：
 * {@code java @libraries/net/neoforged/neoforge/<ver>/<win|unix>_args.txt nogui}。
 *
 * <p>版本映射：NeoForge 版本 = {@code <归一化 MC 版本>.<build>}，1.x 去掉前导 "1."（1.21.8 → 21.8）、
 * 26.x 保留（26.3 → 26.3）。
 */
public final class NeoForgeProvider implements ServerJarProvider {

    private static final String MAVEN = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private static final Pattern VERSION_TAG = Pattern.compile("<version>([^<]+)</version>");

    @Override
    public ServerType type() {
        return ServerType.NEOFORGE;
    }

    @Override
    public List<String> listVersions() {
        Set<String> neo = neoVersions();
        List<String> out = new ArrayList<>();
        for (String mc : new MojangProvider().listVersions()) {   // 已是降序
            String prefix = prefix(mc);
            if (supportedPrefix(prefix) && neo.stream().anyMatch(v -> v.startsWith(prefix + "."))) {
                out.add(mc);
            }
        }
        return out;
    }

    @Override
    public List<String> listLoaderVersions(String gameVersion) {
        String prefix = prefix(gameVersion) + ".";
        List<String> out = new ArrayList<>();
        for (String v : neoVersions()) {
            if (v.startsWith(prefix)) out.add(v);
        }
        out.sort(byBuildDesc(prefix));
        return out;
    }

    @Override
    public InstalledServer install(Path instanceDir, String gameVersion, String loaderVersion,
                                   ProgressListener progress) {
        String loader = (loaderVersion == null || loaderVersion.isBlank())
                ? listLoaderVersions(gameVersion).stream().findFirst()
                        .orElseThrow(() -> new CeobeException("NeoForge 无适配 " + gameVersion + " 的版本"))
                : loaderVersion;

        String installerUrl = MAVEN + "/" + loader + "/neoforge-" + loader + "-installer.jar";
        progress.update("下载 NeoForge 安装器 " + loader, 0, -1);
        Path installer = MojangProvider.cachedJar(
                "neoforge-installer-" + loader + ".jar", installerUrl, trySha1(installerUrl + ".sha1"), null, progress);

        progress.update("运行 NeoForge 安装器（含下载原版与库，可能较慢）", 0, -1);
        runInstaller(installer, instanceDir);

        String argsRel = "libraries/net/neoforged/neoforge/" + loader + "/"
                + (isWindows() ? "win" : "unix") + "_args.txt";
        if (!Files.isRegularFile(instanceDir.resolve(argsRel))) {
            throw new CeobeException("NeoForge 安装未产出 " + argsRel + "，请检查安装器输出");
        }
        progress.update("安装完成", 1, 1);
        return new InstalledServer(argsRel, gameVersion, loader, argsRel);
    }

    /** 归一化：1.x 去掉前导 "1."，其余原样。 */
    static String prefix(String mcVersion) {
        return mcVersion.startsWith("1.") ? mcVersion.substring(2) : mcVersion;
    }

    /** NeoForge 仅支持 MC ≥ 1.20.2，用主版本号下限过滤掉伪匹配（如 "1.0" → "0"）。 */
    private static boolean supportedPrefix(String prefix) {
        int dot = prefix.indexOf('.');
        if (dot <= 0) return false;
        try {
            return Integer.parseInt(prefix.substring(0, dot)) >= 20;
        } catch (NumberFormatException e) {
            return false;
        }
    }

    /** maven-metadata.xml 是 XML，用正则取 <version>（不引入 XML 依赖）。 */
    private static Set<String> neoVersions() {
        String xml = Http.get(MAVEN + "/maven-metadata.xml");
        Set<String> out = new LinkedHashSet<>();
        Matcher m = VERSION_TAG.matcher(xml);
        while (m.find()) out.add(m.group(1));
        return out;
    }

    /** 按 build 号（前缀后的末段数字）降序——字符串序会把 "0.9" 排在 "0.83" 之前。 */
    private static Comparator<String> byBuildDesc(String prefix) {
        return Comparator.<String>comparingLong(v -> {
            String suffix = v.substring(prefix.length());
            int dot = suffix.lastIndexOf('.');
            try {
                return Long.parseLong(dot >= 0 ? suffix.substring(dot + 1) : suffix);
            } catch (NumberFormatException e) {
                return 0L;
            }
        }).reversed();
    }

    private static String trySha1(String url) {
        try {
            String s = Http.get(url).trim();
            int sp = s.indexOf(' ');
            return (sp > 0 ? s.substring(0, sp) : s).trim();
        } catch (RuntimeException e) {
            return null;   // 校验缺失时退化为仅下载
        }
    }

    private static void runInstaller(Path installer, Path instanceDir) {
        try {
            ProcessBuilder pb = new ProcessBuilder(javaBinary(), "-jar", installer.toAbsolutePath().toString(),
                    "--installServer", instanceDir.toAbsolutePath().toString())
                    .directory(instanceDir.toFile());
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
                throw CeobeException.of("NeoForge 安装器退出码 %d\n%s", code, tail(out.toString()));
            }
        } catch (IOException e) {
            throw new CeobeException("无法启动 NeoForge 安装器: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new CeobeException("NeoForge 安装被中断", e);
        }
    }

    private static String javaBinary() {
        Path java = Path.of(System.getProperty("java.home"), "bin", "java" + (isWindows() ? ".exe" : ""));
        return Files.isRegularFile(java) ? java.toString() : "java";
    }

    private static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase().contains("win");
    }

    private static String tail(String text) {
        String[] lines = text.split("\n");
        int from = Math.max(0, lines.length - 12);
        return String.join("\n", java.util.Arrays.copyOfRange(lines, from, lines.length));
    }
}
