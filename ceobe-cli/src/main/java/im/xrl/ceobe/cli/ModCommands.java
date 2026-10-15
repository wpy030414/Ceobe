package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.mods.ModEntry;
import im.xrl.ceobe.core.mods.ModrinthClient;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Json;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.Parameters;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;

/** Modrinth 模组命令族。 */
public final class ModCommands {

    @Command(name = "mod", description = "Modrinth 模组搜索与安装",
            subcommands = {Search.class, Install.class, Rm.class, Ls.class})
    static final class Root implements Callable<Integer> {
        @Override
        public Integer call() {
            Output.info("用法: ceobe mod <search|install|ls|rm> …");
            return 0;
        }
    }

    @Command(name = "search", description = "搜索适配该实例的模组")
    static final class Search implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<关键词>", arity = "0..1", defaultValue = "") String query;

        @Option(names = "--limit", defaultValue = "15") int limit;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            ServerType type = status.meta().serverType;
            if (!type.hasModsDir()) {
                throw new CeobeException("%s 不支持 mods 目录（无模组加载器）".formatted(type.zhLabel()));
            }
            List<ModrinthClient.SearchHit> hits =
                    new ModrinthClient().search(query, status.meta().gameVersion, type, limit);
            if (Output.json) {
                List<Object> list = new ArrayList<>();
                for (var h : hits) {
                    list.add(Output.map("projectId", h.projectId(), "slug", h.slug(), "title", h.title(),
                            "description", h.description(), "author", h.author(),
                            "downloads", h.downloads(), "categories", h.categories()));
                }
                Output.json(Output.map("query", query, "gameVersion", status.meta().gameVersion,
                        "loader", type.name(), "hits", list));
                return 0;
            }
            if (hits.isEmpty()) {
                Output.info("没有找到适配 %s + %s 的模组",
                        status.meta().gameVersion, type.name().toLowerCase());
                return 0;
            }
            List<String[]> rows = new ArrayList<>();
            for (var h : hits) {
                rows.add(new String[]{h.slug(), h.title(), h.author(),
                        String.valueOf(h.downloads()),
                        String.join(",", h.categories().stream().limit(3).toList())});
            }
            Output.table(new String[]{"标识", "名称", "作者", "下载量", "分类"}, rows);
            Output.info("安装: ceobe mod install %s <标识>", instance);
            return 0;
        }
    }

    @Command(name = "install", aliases = {"add"}, description = "安装模组（自动选适配版本）")
    static final class Install implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<模组标识>") String project;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            ServerType type = status.meta().serverType;
            if (!type.hasModsDir()) {
                throw new CeobeException("%s 不支持 mods 目录（无模组加载器）".formatted(type.zhLabel()));
            }
            long[] last = {0};
            ModEntry entry = new ModrinthClient().install(manager.dir(instance), type, project,
                    status.meta().gameVersion, (stage, done, total) -> {
                        if (Output.json) return;
                        long now = System.currentTimeMillis();
                        if (now - last[0] < 700 && done != total) return;
                        last[0] = now;
                        if (total > 0) System.out.printf("\r%-44s %d%%", stage, done * 100 / total);
                        else System.out.printf("\r%-44s", stage);
                        System.out.flush();
                    });
            if (!Output.json) System.out.println();
            // 写回实例元数据
            var meta = status.meta();
            meta.mods.removeIf(m -> m.fileName.equals(entry.fileName));
            meta.mods.add(entry);
            writeMeta(manager.dir(instance), meta);

            if (Output.json) {
                Output.json(Output.map("instance", instance, "file", entry.fileName,
                        "version", entry.versionNumber, "envServer", entry.envServer,
                        "envClient", entry.envClient));
            } else {
                Output.info("已安装 %s → mods/%s（服务端侧: %s）",
                        entry.title, entry.fileName, entry.envServer);
            }
            return 0;
        }
    }

    @Command(name = "rm", aliases = {"uninstall", "remove"}, description = "卸载模组（按文件名或标识）")
    static final class Rm implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<标识|文件名>") String target;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            var meta = status.meta();
            var match = meta.mods.stream()
                    .filter(m -> m.fileName.equals(target) || m.slug.equals(target)
                            || m.projectId.equals(target))
                    .findFirst()
                    .orElseThrow(() -> new CeobeException("实例中未找到模组: " + target));
            new ModrinthClient().uninstall(manager.dir(instance), match);
            meta.mods.remove(match);
            writeMeta(manager.dir(instance), meta);
            Output.info("已卸载 %s（%s）", match.title, match.fileName);
            return 0;
        }
    }

    @Command(name = "ls", aliases = {"list"}, description = "列出已安装模组")
    static final class Ls implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            List<String[]> rows = new ArrayList<>();
            for (ModEntry m : status.meta().mods) {
                boolean present = Files.isRegularFile(
                        manager.dir(instance).resolve("mods").resolve(m.fileName));
                rows.add(new String[]{m.fileName, m.title, m.versionNumber, m.envServer,
                        present ? "是" : "缺失"});
            }
            if (Output.json) {
                List<Object> list = new ArrayList<>();
                for (ModEntry m : status.meta().mods) {
                    list.add(Output.map("file", m.fileName, "title", m.title, "version", m.versionNumber,
                            "envServer", m.envServer, "envClient", m.envClient, "sha1", m.sha1));
                }
                Output.json(Output.map("instance", instance, "mods", list));
                return 0;
            }
            if (rows.isEmpty()) {
                Output.info("实例 %s 尚未安装模组", instance);
                return 0;
            }
            Output.table(new String[]{"文件", "名称", "版本", "服务端侧", "文件存在"}, rows);
            return 0;
        }
    }

    private static void writeMeta(Path dir, im.xrl.ceobe.core.model.InstanceMeta meta) {
        try {
            Files.writeString(dir.resolve("instance.json"), Json.write(meta));
        } catch (Exception e) {
            throw new CeobeException("写入实例元数据失败: " + e.getMessage(), e);
        }
    }

    private ModCommands() {
    }
}
