package im.xrl.ceobe.core.jdk;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Optional;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** 运行期 Java 主版本探测（当前 JVM 或指定 javaHome）。 */
public final class JavaEnv {

    private static final Pattern RELEASE_VERSION = Pattern.compile("JAVA_VERSION=\"([^\"]+)\"");
    private static final Pattern VERSION_OUT = Pattern.compile("version \"([^\"]+)\"");

    /** 当前 JVM 的主版本。 */
    public static int runtimeMajor() {
        return parseMajor(System.getProperty("java.version", ""));
    }

    /** 指定 javaHome 的主版本；先读 release 文件，退化则跑 {@code java -version}。 */
    public static Optional<Integer> majorOf(Path javaHome) {
        if (javaHome == null) return Optional.empty();
        Path release = javaHome.resolve("release");
        try {
            if (Files.isRegularFile(release)) {
                Matcher m = RELEASE_VERSION.matcher(Files.readString(release, StandardCharsets.UTF_8));
                if (m.find()) return Optional.of(parseMajor(m.group(1)));
            }
        } catch (IOException ignored) {
            // 落到进程探测
        }
        return fromProcess(javaHome);
    }

    /** "25.0.1"→25；"21.0.12.1"→21；"1.8.0_402"→8。 */
    static int parseMajor(String version) {
        if (version == null) return 0;
        String v = version.trim();
        if (v.startsWith("1.")) {
            int dot = v.indexOf('.', 2);
            v = v.substring(2, dot < 0 ? v.length() : dot);
        } else {
            int dot = v.indexOf('.');
            if (dot > 0) v = v.substring(0, dot);
            int dash = v.indexOf('-');
            if (dash > 0) v = v.substring(0, dash);
        }
        try {
            return Integer.parseInt(v);
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    private static Optional<Integer> fromProcess(Path javaHome) {
        Path java = javaHome.resolve("bin").resolve(JdkStore.javaExe());
        if (!Files.isRegularFile(java)) return Optional.empty();
        try {
            Process p = new ProcessBuilder(java.toString(), "-version").redirectErrorStream(true).start();
            String out = new String(p.getInputStream().readAllBytes(), StandardCharsets.UTF_8);
            p.waitFor();
            Matcher m = VERSION_OUT.matcher(out);
            return m.find() ? Optional.of(parseMajor(m.group(1))) : Optional.empty();
        } catch (IOException e) {
            return Optional.empty();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return Optional.empty();
        }
    }

    private JavaEnv() {
    }
}
