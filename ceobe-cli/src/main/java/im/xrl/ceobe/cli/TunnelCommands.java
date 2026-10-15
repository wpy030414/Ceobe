package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.runtime.InstanceControl;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.tunnel.TunnelKind;
import im.xrl.ceobe.core.util.CeobeException;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.Parameters;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;

/**
 * 穿透命令族。
 * <p>通道经实例控制通道下发（由监督进程持有），因此实例必须处于运行中。
 */
public final class TunnelCommands {

    @Command(name = "tunnel", description = "内网穿透通道管理",
            subcommands = {Up.class, Down.class, Status.class})
    static final class Root implements Callable<Integer> {
        @Override
        public Integer call() {
            Output.info("用法: ceobe tunnel <up|down|status> <实例> [--kind pinggy|cloudflare|lan]");
            return 0;
        }
    }

    @Command(name = "up", description = "开启通道（pinggy=玩家连接，cloudflare=Web 控制台）")
    static final class Up implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;

        @Option(names = "--kind", defaultValue = "pinggy",
                description = "通道类型: pinggy | cloudflare | lan（默认 pinggy）")
        String kind;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            if (!status.state().isLive()) {
                throw new CeobeException("实例 %s 未在运行，请先 `ceobe start -d %s`".formatted(instance, instance));
            }
            if (!InstanceControl.isAvailable(manager.dir(instance))) {
                throw new CeobeException("实例 %s 无控制通道（需以受控方式启动）".formatted(instance));
            }
            TunnelKind k = TunnelKind.fromId(kind);
            List<String> result = InstanceControl.call(manager.dir(instance), "TUNNEL UP " + k.id());
            String url = result.isEmpty() ? "(未返回地址)" : result.get(0);
            String expires = result.size() > 1 ? result.get(1) : "";
            if (Output.json) {
                Output.json(Output.map("instance", instance, "kind", k.id(),
                        "url", url, "expiresAt", expires));
            } else {
                Output.info("通道已开启 [%s]", k.zhLabel());
                Output.info("地址: %s", url);
                if (!expires.isEmpty()) Output.info("有效期至: %s（到期前自动续期）", expires);
                if (k == TunnelKind.PINGGY) {
                    Output.info("把该地址发给朋友，在「多人游戏 → 直接连接」中填入即可。");
                } else if (k == TunnelKind.CLOUDFLARE) {
                    Output.info("浏览器打开该地址即可远程查看服务器状态（只读）。");
                }
            }
            return 0;
        }
    }

    @Command(name = "down", description = "关闭通道")
    static final class Down implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;

        @Option(names = "--kind", defaultValue = "all", description = "通道类型（默认全部关闭）")
        String kind;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            manager.require(instance);
            var dir = manager.dir(instance);
            if (!InstanceControl.isAvailable(dir)) {
                throw new CeobeException("实例 %s 无控制通道".formatted(instance));
            }
            if ("all".equalsIgnoreCase(kind)) {
                for (TunnelKind k : TunnelKind.values()) {
                    try {
                        InstanceControl.call(dir, "TUNNEL DOWN " + k.id());
                    } catch (RuntimeException ignored) {
                    }
                }
            } else {
                InstanceControl.call(dir, "TUNNEL DOWN " + TunnelKind.fromId(kind).id());
            }
            Output.info("通道已关闭: %s", instance);
            return 0;
        }
    }

    @Command(name = "status", description = "查看通道状态与地址")
    static final class Status implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<实例>") String instance;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(instance);
            var dir = manager.dir(instance);
            List<String[]> rows = new ArrayList<>();
            if (InstanceControl.isAvailable(dir)) {
                for (String line : InstanceControl.call(dir, "TUNNEL STATUS")) {
                    String[] parts = line.split("\\|", -1);
                    if (parts.length >= 2) {
                        rows.add(new String[]{label(parts[0]), parts[1],
                                parts.length > 2 ? parts[2] : ""});
                    }
                }
            }
            if (Output.json) {
                List<Object> list = new ArrayList<>();
                for (String[] r : rows) {
                    list.add(Output.map("kind", r[0], "url", r[1], "expiresAt", r[2]));
                }
                Output.json(Output.map("instance", instance,
                        "running", status.state().isLive(),
                        "localPort", status.meta().port,
                        "tunnels", list));
                return 0;
            }
            Output.info("实例 %s（%s）本机端口 %d",
                    instance, status.state().zhLabel(), status.meta().port);
            if (rows.isEmpty()) {
                Output.info("当前无活动通道。开启: ceobe tunnel up %s", instance);
                return 0;
            }
            Output.table(new String[]{"通道", "公网地址", "有效期至"}, rows);
            return 0;
        }

        private static String label(String id) {
            try {
                return TunnelKind.fromId(id).zhLabel();
            } catch (IllegalArgumentException e) {
                return id;
            }
        }
    }

    private TunnelCommands() {
    }
}
