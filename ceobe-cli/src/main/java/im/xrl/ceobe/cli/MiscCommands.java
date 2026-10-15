package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.provider.ProviderRegistry;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.tunnel.CloudflareQuickTunnel;
import im.xrl.ceobe.core.tunnel.LanDirect;
import im.xrl.ceobe.core.tunnel.PinggyTunnel;
import im.xrl.ceobe.core.tunnel.TunnelKind;
import im.xrl.ceobe.core.tunnel.TunnelProvider;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.webconsole.WebConsole;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.Parameters;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Callable;
import java.util.concurrent.CountDownLatch;

/** 版本查询 / 环境自检 / 独立 Web 控制台。 */
public final class MiscCommands {

    @Command(name = "versions", description = "列出服务端可选版本（按家族归组）")
    static final class Versions implements Callable<Integer> {
        @Option(names = "--type", defaultValue = "paper",
                description = "服务端类型: vanilla | paper | fabric | purpur")
        String type;

        @Option(names = "--all", description = "显示全部版本（默认每组显示最新 6 个）")
        boolean all;

        @Override
        public Integer call() {
            ServerType serverType = InstanceCommands.parseType(type);
            List<String> versions = ProviderRegistry.of(serverType).listVersions();
            Map<String, List<String>> grouped = new LinkedHashMap<>();
            for (String v : versions) {
                grouped.computeIfAbsent(im.xrl.ceobe.core.util.Versions.family(v), k -> new ArrayList<>()).add(v);
            }
            if (Output.json) {
                Output.json(Output.map("type", serverType.name(),
                        "latest", versions.isEmpty() ? null : versions.get(0),
                        "families", grouped));
                return 0;
            }
            if (versions.isEmpty()) {
                Output.info("未获取到版本列表");
                return 0;
            }
            Output.info("最新正式版: %s", versions.get(0));
            List<String[]> rows = new ArrayList<>();
            grouped.forEach((family, list) -> rows.add(new String[]{
                    family,
                    String.join("  ", all ? list : list.stream().limit(6).toList())
                            + (!all && list.size() > 6 ? "  …" : "")}));
            Output.table(new String[]{"家族", "版本"}, rows);
            return 0;
        }
    }

    @Command(name = "doctor", description = "环境自检（JDK / 穿透工具 / 目录）")
    static final class Doctor implements Callable<Integer> {
        @Override
        public Integer call() {
            List<String[]> rows = new ArrayList<>();
            boolean ok = true;

            String javaVersion = System.getProperty("java.version");
            boolean javaOk = Runtime.version().feature() >= 21;
            rows.add(new String[]{"JDK", javaVersion + "（" + System.getProperty("java.home") + "）",
                    javaOk ? "OK" : "需 21+（26.x 需 25）"});
            ok &= javaOk;

            boolean cfOk = new CloudflareQuickTunnel().available();
            rows.add(new String[]{"cloudflared（Web 控制台穿透）", cfOk ? "已安装" : "未找到",
                    cfOk ? "OK" : "可选：https://developers.cloudflare.com/cloudflare-one/"});
            boolean sshOk = new PinggyTunnel().available();
            rows.add(new String[]{"ssh（Pinggy 玩家通道）", sshOk ? "已安装" : "未找到",
                    sshOk ? "OK" : "Windows 设置 → 可选功能 → OpenSSH 客户端"});
            boolean lanOk = new LanDirect().available();
            rows.add(new String[]{"局域网地址", lanOk ? String.join("、", LanDirect.localAddresses()) : "无",
                    lanOk ? "OK" : "未连接任何网络"});

            var home = CeobeHome.root();
            boolean writable = Files.isWritable(CeobeHome.ensure(home));
            rows.add(new String[]{"ceobe 主目录", home.toString(), writable ? "OK" : "不可写"});
            ok &= writable;

            int instances = new InstanceManager().list().size();
            rows.add(new String[]{"已有实例", String.valueOf(instances), "—"});

            if (Output.json) {
                List<Object> list = new ArrayList<>();
                for (String[] r : rows) {
                    list.add(Output.map("check", r[0], "detail", r[1], "status", r[2]));
                }
                Output.json(Output.map("ok", ok, "checks", list));
                return ok ? 0 : 1;
            }
            Output.table(new String[]{"检查项", "详情", "结论"}, rows);
            Output.info(ok ? "环境可用 ✓" : "存在问题，请按上表处理");
            return ok ? 0 : 1;
        }
    }

    @Command(name = "web", description = "以无头方式运行 Web 控制台（供 CF 隧道对外暴露）")
    static final class Web implements Callable<Integer> {
        @Option(names = "--port", defaultValue = "0", description = "监听端口（0=随机）")
        int port;

        @Parameters(index = "0", paramLabel = "<实例>", arity = "0..1",
                description = "可选：直接把某实例的 CF 通道也开启")
        String instance;

        @Override
        public Integer call() throws Exception {
            InstanceManager manager = new InstanceManager();
            try (WebConsole console = new WebConsole(manager, port)) {
                Output.info("Web 控制台已启动: http://127.0.0.1:%d", console.port());
                if (instance != null && InstanceControlAvailable(manager, instance)) {
                    var tm = new im.xrl.ceobe.core.tunnel.TunnelManager(manager);
                    tm.webConsolePort(console.port());
                    var session = tm.up(instance, TunnelKind.CLOUDFLARE);
                    Output.info("Cloudflare 通道: %s", session.publicUrl);
                }
                Output.info("按 Ctrl+C 结束。");
                new CountDownLatch(1).await();
            }
            return 0;
        }

        private static boolean InstanceControlAvailable(InstanceManager manager, String instance) {
            manager.require(instance);
            return im.xrl.ceobe.core.runtime.InstanceControl.isAvailable(manager.dir(instance));
        }
    }

    /** 供 doctor 复用的通道预检（保留扩展位）。 */
    static Map<String, Boolean> tunnelAvailability() {
        Map<String, Boolean> m = new LinkedHashMap<>();
        for (TunnelProvider p : List.of(new PinggyTunnel(), new CloudflareQuickTunnel(), new LanDirect())) {
            m.put(p.kind().id(), p.available());
        }
        return m;
    }

    private MiscCommands() {
    }
}
