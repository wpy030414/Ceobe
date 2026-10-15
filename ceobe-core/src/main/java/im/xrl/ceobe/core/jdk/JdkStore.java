package im.xrl.ceobe.core.jdk;

import im.xrl.ceobe.core.store.CeobeHome;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/** ceobe 自管 JDK 仓库：{@code ~/.ceobe/jdk/<name>/}（每个子目录含 bin/java）。 */
public final class JdkStore {

    public record InstalledJdk(int major, String name, Path home) {
    }

    public static Path root() {
        return CeobeHome.ensure(CeobeHome.root().resolve("jdk"));
    }

    /** 扫描已安装 JDK，按主版本降序。 */
    public static List<InstalledJdk> list() {
        List<InstalledJdk> out = new ArrayList<>();
        try (var stream = Files.list(root())) {
            stream.filter(Files::isDirectory).forEach(dir -> {
                if (!Files.isRegularFile(dir.resolve("bin").resolve(javaExe()))) return;
                JavaEnv.majorOf(dir).ifPresent(major ->
                        out.add(new InstalledJdk(major, dir.getFileName().toString(), dir)));
            });
        } catch (IOException ignored) {
        }
        out.sort((a, b) -> Integer.compare(b.major(), a.major()));
        return out;
    }

    /** 找主版本匹配的自管 JDK。 */
    public static Optional<Path> findMajor(int major) {
        return list().stream().filter(j -> j.major() == major).map(InstalledJdk::home).findFirst();
    }

    static String javaExe() {
        return System.getProperty("os.name", "").toLowerCase().contains("win") ? "java.exe" : "java";
    }

    private JdkStore() {
    }
}
