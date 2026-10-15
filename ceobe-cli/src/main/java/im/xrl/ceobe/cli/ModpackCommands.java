package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.model.InstanceSpec;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.mods.ModrinthClient;
import im.xrl.ceobe.core.mods.MrpackInstaller;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Http;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.Parameters;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;

/** Modrinth 整合包（.mrpack）命令族。 */
public final class ModpackCommands {

    @Command(name = "modpack", description = "Modrinth 整合包（.mrpack）搜索与安装",
            subcommands = {Search.class, Info.class, Install.class, New.class})
    static final class Root implements Callable<Integer> {
        @Override
        public Integer call() {
            Output.info("用法: ceobe modpack <search|info|install|new> …");
            return 0;
        }
    }

    @Command(name = "search", description = "搜索整合包")
    static final class Search implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<关键词>", arity = "0..1", defaultValue = "") String query;
        @Option(names = "--limit", defaultValue = "15") int limit;
        @Option(names = "--version", paramLabel = "VER", description = "按 MC 版本过滤") String version;
        @Option(names = "--loader", paramLabel = "L", description = "按加载器过滤: fabric | neoforge") String loader;

        @Override
        public Integer call() {
            List<ModrinthClient.PackHit> hits = new ModrinthClient().searchPacks(query, version, loader, limit);
            if (Output.json) {
                List<Object> list = new ArrayList<>();
                for (var h : hits) {
                    list.add(Output.map("slug", h.slug(), "title", h.title(), "author", h.author(),
                            "downloads", h.downloads(), "categories", h.categories()));
                }
                Output.json(Output.map("query", query, "hits", list));
                return 0;
            }
            if (hits.isEmpty()) {
                Output.info("没有匹配的整合包");
                return 0;
            }
            List<String[]> rows = new ArrayList<>();
            for (var h : hits) {
                rows.add(new String[]{h.slug(), h.title(), h.author(),
                        String.valueOf(h.downloads()),
                        String.join(",", h.categories().stream().limit(3).toList())});
            }
            Output.table(new String[]{"标识", "名称", "作者", "下载量", "分类"}, rows);
            Output.info("一键创建: ceobe modpack new <名称> <标识>");
            return 0;
        }
    }

    @Command(name = "info", description = "查看整合包的版本 / 加载器 / 文件")
    static final class Info implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<标识>") String pack;
        @Option(names = "--version", paramLabel = "VER", description = "限定 MC 版本") String version;
        @Option(names = "--loader", paramLabel = "L", description = "限定加载器") String loader;

        @Override
        public Integer call() {
            var pv = new ModrinthClient().resolvePack(pack, version, loader);
            if (Output.json) {
                Output.json(Output.map("slug", pack, "name", pv.name(), "version", pv.versionNumber(),
                        "gameVersions", pv.gameVersions(), "loaders", pv.loaders(),
                        "url", pv.url(), "size", pv.size()));
                return 0;
            }
            Output.info("整合包: %s", pv.name());
            Output.info("版本: %s（MC %s / %s）", pv.versionNumber(),
                    pv.gameVersions().isEmpty() ? "?" : pv.gameVersions().get(0),
                    pv.loaders().isEmpty() ? "?" : String.join(",", pv.loaders()));
            Output.info("文件: %s（%d bytes）", pv.url(), pv.size());
            return 0;
        }
    }

    @Command(name = "install", description = "把整合包安装到已有实例（需加载器与版本一致）")
    static final class Install implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;
        @Parameters(index = "1", paramLabel = "<标识>") String pack;
        @Option(names = "--version", paramLabel = "VER", description = "限定整合包 MC 版本") String version;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            if (status.state().isLive()) {
                throw CeobeException.of("实例 %s 正在运行，请先停止再装整合包", instance);
            }
            Path zip = downloadPack(pack, version, loaderOf(status.meta().serverType));
            MrpackInstaller.Index idx = MrpackInstaller.readIndex(zip);
            checkCompat(status, idx);

            long[] last = {0};
            MrpackInstaller.Result r = MrpackInstaller.install(manager.dir(instance), zip, printer(last));
            if (!Output.json) System.out.println();
            if (Output.json) {
                Output.json(Output.map("instance", instance, "pack", r.name(),
                        "version", r.versionId(), "files", r.files(), "skipped", r.skipped(),
                        "overrides", r.overrides()));
            } else {
                Output.info("已安装整合包「%s」→ 实例 %s（%d 个文件，跳过 %d，覆盖 %d）",
                        r.name(), instance, r.files(), r.skipped(), r.overrides());
            }
            return 0;
        }
    }

    @Command(name = "new", description = "按整合包自动创建实例并安装（一键）")
    static final class New implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;
        @Parameters(index = "1", paramLabel = "<标识>") String pack;
        @Option(names = "--mem", defaultValue = "4G", description = "内存上限，如 4G") String mem;
        @Option(names = "--port", defaultValue = "25565", description = "端口或 auto") String port;
        @Option(names = "--jvm", defaultValue = "", description = "附加 JVM 参数") String jvmArgs;
        @Option(names = "--java-home", paramLabel = "PATH", description = "指定 JRE 目录") String javaHome;

        @Override
        public Integer call() {
            Path zip = downloadPack(pack, null, null);
            MrpackInstaller.Index idx = MrpackInstaller.readIndex(zip);
            if (idx.mcVersion() == null) {
                throw new CeobeException("整合包未声明 MC 版本，无法自动创建");
            }
            ServerType type = typeOfLoader(idx.loader());

            InstanceManager manager = new InstanceManager();
            int portNum = InstanceCommands.resolvePort(port, manager);
            InstanceSpec spec = new InstanceSpec(name, type, idx.mcVersion(), idx.loaderVersion(),
                    InstanceCommands.parseMemory(mem), jvmArgs, portNum, javaHome);

            long[] last = {0};
            manager.create(spec, printer(last));
            if (!Output.json) System.out.println();
            MrpackInstaller.Result r = MrpackInstaller.install(manager.dir(name), zip, printer(last));
            if (!Output.json) System.out.println();
            if (Output.json) {
                Output.json(Output.map("instance", name, "type", type.name(), "mcVersion", idx.mcVersion(),
                        "loader", idx.loader(), "loaderVersion", idx.loaderVersion(),
                        "pack", r.name(), "files", r.files()));
            } else {
                Output.info("已创建实例 %s（%s %s / %s），并安装整合包「%s」（%d 个文件）",
                        name, type.name(), idx.mcVersion(), idx.loader(), r.name(), r.files());
                Output.info("启动: ceobe start %s", name);
            }
            return 0;
        }
    }

    // —— 辅助 ——

    /** 下载 .mrpack 到缓存目录，返回本地路径。 */
    static Path downloadPack(String pack, String version, String loader) {
        return MrpackInstaller.fetch(new ModrinthClient().resolvePack(pack, version, loader),
                (s, d, t) -> {
                });
    }

    static String loaderOf(ServerType type) {
        return type.loaderId();
    }

    static ServerType typeOfLoader(String loader) {
        return ServerType.fromLoaderId(loader);
    }

    static void checkCompat(InstanceStatus status, MrpackInstaller.Index idx) {
        if (idx.mcVersion() != null && !idx.mcVersion().equals(status.meta().gameVersion)) {
            throw CeobeException.of("实例是 MC %s，整合包要求 MC %s；请创建匹配的实例",
                    status.meta().gameVersion, idx.mcVersion());
        }
        String want = loaderOf(status.meta().serverType);
        if (idx.loader() != null && want != null && !idx.loader().equals(want)) {
            throw CeobeException.of("实例加载器是 %s，整合包要求 %s", want, idx.loader());
        }
        if (want == null) {
            throw CeobeException.of("%s 不支持装整合包（加载器不对）",
                    status.meta().serverType.zhLabel());
        }
    }

    static ProgressListener printer(long[] last) {
        return (stage, done, total) -> {
            if (Output.json) return;
            long now = System.currentTimeMillis();
            if (now - last[0] < 700 && done != total) return;
            last[0] = now;
            if (total > 0) System.out.printf("\r%-44s %d%%", stage, done * 100 / total);
            else System.out.printf("\r%-44s", stage);
            System.out.flush();
        };
    }

    private ModpackCommands() {
    }
}
