package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.jdk.JdkInstaller;
import im.xrl.ceobe.core.jdk.JdkStore;
import im.xrl.ceobe.core.jdk.JavaEnv;
import im.xrl.ceobe.core.jdk.JavaRequirement;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.util.CeobeException;
import picocli.CommandLine.Command;
import picocli.CommandLine.Parameters;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;

/** JDK 自检与一键安装命令族。 */
public final class JdkCommands {

    @Command(name = "jdk", description = "JDK 检查、一键安装与按实例指定",
            subcommands = {ListCmd.class, Install.class, Required.class, Use.class})
    static final class Root implements Callable<Integer> {
        @Override
        public Integer call() {
            Output.info("用法: ceobe jdk <list | install <主版本> | required <游戏版本> | use <实例> <主版本>>");
            return 0;
        }
    }

    @Command(name = "list", aliases = {"ls"}, description = "列出自管 JDK 与当前运行期 Java")
    static final class ListCmd implements Callable<Integer> {
        @Override
        public Integer call() {
            int runtime = JavaEnv.runtimeMajor();
            List<JdkStore.InstalledJdk> jdks = JdkStore.list();
            if (Output.json) {
                List<Object> l = new ArrayList<>();
                for (var j : jdks) {
                    l.add(Output.map("major", j.major(), "name", j.name(), "home", j.home().toString()));
                }
                Output.json(Output.map("runtimeMajor", runtime, "installed", l));
                return 0;
            }
            Output.info("当前运行期 Java: %d", runtime);
            if (jdks.isEmpty()) {
                Output.info("尚未安装 ceobe 自管 JDK。安装: ceobe jdk install <主版本>");
                return 0;
            }
            List<String[]> rows = new ArrayList<>();
            for (var j : jdks) rows.add(new String[]{String.valueOf(j.major()), j.name(), j.home().toString()});
            Output.table(new String[]{"Java", "名称", "路径"}, rows);
            return 0;
        }
    }

    @Command(name = "install", description = "一键安装指定主版本的 Temurin JDK（按平台自动选包）")
    static final class Install implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<主版本>", description = "如 21 / 17 / 8")
        int major;

        @Override
        public Integer call() {
            long[] last = {0};
            Path home = JdkInstaller.install(major, (stage, done, total) -> {
                if (Output.json) return;
                long now = System.currentTimeMillis();
                if (now - last[0] < 700 && done != total) return;
                last[0] = now;
                if (total > 0) System.out.printf("\r%-40s %d%%", stage, done * 100 / total);
                else System.out.printf("\r%-40s", stage);
                System.out.flush();
            });
            if (!Output.json) System.out.println();
            if (Output.json) {
                Output.json(Output.map("major", major, "home", home.toString()));
            } else {
                Output.info("已安装 Java %d → %s", major, home);
                Output.info("新建实例会自动选用它；已有实例用 `ceobe jdk use <实例> %d`", major);
            }
            return 0;
        }
    }

    @Command(name = "required", description = "查询某游戏版本所需的 Java 主版本")
    static final class Required implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<游戏版本>") String version;

        @Override
        public Integer call() {
            int req = JavaRequirement.requiredMajor(version);
            if (Output.json) Output.json(Output.map("version", version, "requiredJava", req));
            else Output.info("%s 需要 Java %d", version, req);
            return 0;
        }
    }

    @Command(name = "use", description = "把某实例的 Java 指向自管 JDK（按主版本）")
    static final class Use implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<主版本>") int major;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            if (manager.require(instance).state().isLive()) {
                throw CeobeException.of("实例 %s 正在运行，请先停止", instance);
            }
            Path home = JdkStore.findMajor(major).orElseThrow(() -> new CeobeException(
                    "未安装 Java %d；先运行 `ceobe jdk install %d`".formatted(major, major)));
            manager.setJavaHome(instance, home.toString());
            Output.info("实例 %s 的 Java 已指向 Java %d → %s", instance, major, home);
            return 0;
        }
    }

    private JdkCommands() {
    }
}
