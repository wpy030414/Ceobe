package im.xrl.ceobe.core.jdk;

import java.nio.file.Path;
import java.util.Optional;

/** 为某实例解析应当使用的 JavaHome，并给出是否需要提醒用户（一键装 / 手动指定）。 */
public final class JavaResolver {

    /**
     * @param required        该 MC 版本所需的 Java 主版本
     * @param actual          实际将使用的 Java 主版本（-1 = 未知）
     * @param javaHome        建议的 javaHome（null = 继承当前 JVM）
     * @param ok              是否满足
     * @param message         需要提醒用户的说明（ok 时可为 null）
     */
    public record Resolution(int required, int actual, Path javaHome, boolean ok, String message) {
    }

    /** @param explicitJavaHome 用户显式指定的 JRE 目录（可空） */
    public static Resolution resolve(String gameVersion, String explicitJavaHome) {
        int required = JavaRequirement.requiredMajor(gameVersion);

        if (explicitJavaHome != null && !explicitJavaHome.isBlank()) {
            Path home = Path.of(explicitJavaHome);
            int actual = JavaEnv.majorOf(home).orElse(-1);
            boolean ok = actual == required && JavaEnv.hasLauncher(home);
            String message = ok ? null
                    : !JavaEnv.hasLauncher(home)
                            ? "指定的 JRE 缺少 java 启动器（bin/java）: " + home
                            : "指定的 JRE 为 Java %s，但 %s 需要 Java %d".formatted(
                                    actual < 0 ? "未知" : actual, gameVersion, required);
            return new Resolution(required, actual, home, ok, message);
        }

        // 当前 JVM 可直接作为服务端运行时：主版本匹配且确有 java 启动器可 fork
        Path runtimeHome = JavaEnv.runtimeHome();
        int runtime = JavaEnv.runtimeMajor();
        if (runtime == required && JavaEnv.hasLauncher(runtimeHome)) {
            return new Resolution(required, runtime, null, true, null);
        }

        Optional<Path> installed = JdkStore.findMajor(required);
        if (installed.isPresent()) {
            return new Resolution(required, required, installed.get(), true,
                    "自动选用 ceobe 自管 JDK（Java %d）: %s".formatted(required, installed.get()));
        }

        // 宿主真 JDK（JAVA_HOME 优先，其次当前 JVM）
        Optional<Path> host = JavaEnv.hostJavaHome();
        if (host.isPresent() && JavaEnv.majorOf(host.get()).orElse(-1) == required) {
            return new Resolution(required, required, host.get(), true, null);
        }
        int actual = host.map(h -> JavaEnv.majorOf(h).orElse(-1)).orElse(runtime);
        return new Resolution(required, actual, null, false,
                "%s 需要 Java %d，当前可用 Java %d%s；可运行 `ceobe jdk install %d` 一键安装，或用 --java-home 指定"
                        .formatted(gameVersion, required, actual,
                                host.isEmpty() ? "（未发现带 java 启动器的运行时）" : "", required));
    }

    private JavaResolver() {
    }
}
