package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.config.PropKey;
import im.xrl.ceobe.core.config.PropSchema;
import im.xrl.ceobe.core.config.ServerPropertiesFile;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.util.CeobeException;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.Parameters;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;

/** server.properties 配置命令族（直接操作文件，无需实例在运行）。 */
public final class ConfigCommands {

    @Command(name = "config", description = "查看/修改 server.properties",
            subcommands = {Get.class, Set.class, Ls.class})
    static final class Root implements Callable<Integer> {
        @Override
        public Integer call() {
            Output.info("用法: ceobe config <get|set|ls> <实例> …");
            return 0;
        }
    }

    @Command(name = "get", description = "读取单个配置项")
    static final class Get implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<键>") String key;

        @Override
        public Integer call() {
            ServerPropertiesFile props = load(instance);
            String value = props.get(key);
            if (value == null) {
                PropKey schema = PropSchema.key(key);
                if (schema == null) throw new CeobeException("未知配置项: " + key);
                value = schema.def();
            }
            if (Output.json) {
                Output.json(Output.map("instance", instance, "key", key, "value", value));
            } else {
                System.out.println(value);
            }
            return 0;
        }
    }

    @Command(name = "set", description = "修改配置项（服务器运行时需重启生效）")
    static final class Set implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<键>") String key;
        @Parameters(index = "2", paramLabel = "<值>") String value;

        @Override
        public Integer call() {
            ServerPropertiesFile props = load(instance);
            props.set(key, value);
            props.save();
            PropKey schema = PropSchema.key(key);
            Output.info("已设置 %s = %s%s", key, value,
                    schema == null ? "" : "（" + schema.zh() + "）");
            return 0;
        }
    }

    @Command(name = "ls", description = "列出配置项（按 schema 分组）")
    static final class Ls implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;

        @Option(names = "--group", paramLabel = "GROUP", description = "仅显示某分组（如 基础/网络/性能）")
        String group;

        @Override
        public Integer call() {
            ServerPropertiesFile props = load(instance);
            List<String[]> rows = new ArrayList<>();
            for (String g : PropSchema.groups()) {
                if (group != null && !group.equals(g)) continue;
                for (PropKey k : PropSchema.all()) {
                    if (k.legacy() || !g.equals(k.group())) continue;
                    String value = props.contains(k.key()) ? props.get(k.key()) : k.def();
                    rows.add(new String[]{g, k.key(), k.zh(), value});
                }
            }
            if (Output.json) {
                List<Object> list = new ArrayList<>();
                for (String[] r : rows) {
                    list.add(Output.map("group", r[0], "key", r[1], "label", r[2], "value", r[3]));
                }
                Output.json(Output.map("instance", instance, "properties", list));
                return 0;
            }
            Output.table(new String[]{"分组", "键", "含义", "当前值"}, rows);
            return 0;
        }
    }

    static ServerPropertiesFile load(String instance) {
        InstanceManager manager = new InstanceManager();
        Path dir = manager.dir(instance);
        manager.require(instance);
        Path file = dir.resolve("server.properties");
        if (!Files.isRegularFile(file)) {
            throw new CeobeException("实例 %s 尚无 server.properties（先启动一次）".formatted(instance));
        }
        return ServerPropertiesFile.load(file);
    }

    private ConfigCommands() {
    }
}
