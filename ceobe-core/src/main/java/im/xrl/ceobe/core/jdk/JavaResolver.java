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
            boolean ok = actual == required;
            return new Resolution(required, actual, home, ok, ok ? null
                    : "指定的 JRE 为 Java %s，但 %s 需要 Java %d".formatted(
                            actual < 0 ? "未知" : actual, gameVersion, required));
        }

        int runtime = JavaEnv.runtimeMajor();
        if (runtime == required) {
            return new Resolution(required, runtime, null, true, null);
        }
        Optional<Path> installed = JdkStore.findMajor(required);
        if (installed.isPresent()) {
            return new Resolution(required, required, installed.get(), true,
                    "自动选用 ceobe 自管 JDK（Java %d）: %s".formatted(required, installed.get()));
        }
        return new Resolution(required, runtime, null, false,
                "%s 需要 Java %d，当前为 Java %d；可运行 `ceobe jdk install %d` 一键安装，或用 --java-home 指定"
                        .formatted(gameVersion, required, runtime, required));
    }

    private JavaResolver() {
    }
}
