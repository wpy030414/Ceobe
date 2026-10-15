package im.xrl.ceobe.core.store;

import im.xrl.ceobe.core.util.CeobeException;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;

/** ceobe 主目录：默认 ~/.ceobe，可用环境变量 CEOBE_HOME 覆盖（测试用）。 */
public final class CeobeHome {

    /** 主目录解析优先级：系统属性 ceobe.home > 环境变量 CEOBE_HOME > ~/.ceobe。 */
    public static Path root() {
        String prop = System.getProperty("ceobe.home");
        if (prop != null && !prop.isBlank()) {
            return Path.of(prop);
        }
        String env = System.getenv("CEOBE_HOME");
        if (env != null && !env.isBlank()) {
            return Path.of(env);
        }
        String userHome = System.getProperty("user.home");
        if (userHome == null) throw new CeobeException("无法定位用户主目录");
        return Path.of(userHome, ".ceobe");
    }

    public static Path instancesDir() {
        return ensure(root().resolve("instances"));
    }

    public static Path instanceDir(String name) {
        return instancesDir().resolve(name);
    }

    public static Path jarCache() {
        return ensure(root().resolve("cache").resolve("jars"));
    }

    public static Path ensure(Path dir) {
        try {
            Files.createDirectories(dir);
            return dir;
        } catch (IOException e) {
            throw new CeobeException("无法创建目录: " + dir, e);
        }
    }

    private CeobeHome() {
    }
}
