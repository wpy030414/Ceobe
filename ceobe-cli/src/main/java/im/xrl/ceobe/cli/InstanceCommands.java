package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.jdk.JavaEnv;
import im.xrl.ceobe.core.jdk.JavaResolver;
import im.xrl.ceobe.core.model.InstanceSpec;
import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.provider.ProviderRegistry;
import im.xrl.ceobe.core.runtime.InstanceControl;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.CeobeException;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.Parameters;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;

/** 实例生命周期命令族。 */
public final class InstanceCommands {

    @Command(name = "ps", aliases = {"ls", "list"}, description = "列出实例（默认仅运行中）")
    static final class Ps implements Callable<Integer> {
        @Option(names = {"-a", "--all"}, description = "包含已停止实例")
        boolean all;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            List<InstanceStatus> statuses = new ArrayList<>(manager.list());
            if (!all) {
                statuses.removeIf(s -> !s.state().isLive());
            }
            if (Output.json) {
                Output.json(Output.map("instances", statuses.stream().map(s -> Output.map(
                        "name", s.meta().name,
                        "type", s.meta().serverType.name(),
                        "version", s.meta().gameVersion,
                        "state", s.state().name(),
                        "pid", s.pid(),
                        "port", s.meta().port,
                        "address", s.addressLine(),
                        "pinggy", s.pinggyAddress(),
                        "cloudflare", s.cloudflareUrl())).toList()));
                return 0;
            }
            if (statuses.isEmpty()) {
                boolean any = !manager.list().isEmpty();
                Output.info(any
                        ? "无运行中的实例（`ceobe ps -a` 显示全部）"
                        : "暂无实例。用 `ceobe create <名称> --type paper` 创建一个。");
                return 0;
            }
            List<String[]> rows = new ArrayList<>();
            for (InstanceStatus s : statuses) {
                rows.add(new String[]{
                        s.meta().name,
                        s.meta().serverType.name(),
                        s.meta().gameVersion,
                        s.state().zhLabel(),
                        s.state().isLive() ? String.valueOf(s.pid()) : "-",
                        String.valueOf(s.meta().port),
                        s.addressLine()
                });
            }
            Output.table(new String[]{"名称", "类型", "版本", "状态", "PID", "端口", "地址"}, rows);
            return 0;
        }
    }

    @Command(name = "create", description = "创建实例并安装服务端")
    static final class Create implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>", description = "实例名（小写字母/数字/_/-）")
        String name;

        @Option(names = "--type", paramLabel = "TYPE", defaultValue = "paper",
                description = "服务端类型: vanilla | paper | fabric | neoforge | purpur（默认 paper）")
        String type;

        @Option(names = "--version", paramLabel = "VER", description = "游戏版本（默认最新正式版）")
        String version;

        @Option(names = "--loader", paramLabel = "VER", description = "加载器版本（Fabric/NeoForge；默认最新 stable）")
        String loader;

        @Option(names = "--mem", paramLabel = "SIZE", defaultValue = "2G", description = "内存上限，如 4G / 4096M")
        String mem;

        @Option(names = "--port", paramLabel = "PORT", defaultValue = "25565",
                description = "服务器端口；auto = 自动选取空闲端口（默认 25565）")
        String port;

        @Option(names = "--jvm", paramLabel = "ARGS", defaultValue = "", description = "附加 JVM 参数")
        String jvmArgs;

        @Option(names = "--java-home", paramLabel = "PATH", description = "指定 JRE 目录（默认继承当前）")
        String javaHome;

        @Override
        public Integer call() {
            ServerType serverType = parseType(type);
            InstanceManager manager = new InstanceManager();
            var provider = ProviderRegistry.of(serverType);
            String gameVersion = version;
            if (gameVersion == null || gameVersion.isBlank()) {
                gameVersion = provider.listVersions().stream().findFirst()
                        .orElseThrow(() -> new CeobeException("获取版本列表失败"));
                Output.info("未指定版本，使用最新正式版: %s", gameVersion);
            }
            int memoryMb = parseMemory(mem);
            int portNum = resolvePort(port, manager);
            InstanceSpec spec = new InstanceSpec(name, serverType, gameVersion, loader, memoryMb,
                    jvmArgs, portNum, javaHome);

            long[] lastPrint = {0};
            var meta = manager.create(spec, (stage, done, total) -> {
                if (Output.json) return;
                long now = System.currentTimeMillis();
                if (now - lastPrint[0] < 700 && total != done) return;
                lastPrint[0] = now;
                if (total > 0) {
                    System.out.printf("\r%-40s %d%%", stage, done * 100 / total);
                } else {
                    System.out.printf("\r%-40s", stage);
                }
                System.out.flush();
            });
            if (!Output.json) System.out.println();
            if (Output.json) {
                Output.json(Output.map("name", meta.name, "type", meta.serverType.name(),
                        "version", meta.gameVersion, "loader", meta.loaderVersion,
                        "port", meta.port, "memoryMb", meta.memoryMb,
                        "dir", manager.dir(name).toString()));
            } else {
                Output.info("实例 %s 创建完成（%s %s，端口 %d，内存 %dMB）",
                        meta.name, meta.serverType.name(), meta.gameVersion, meta.port, meta.memoryMb);
                Output.info("启动: ceobe start %s", meta.name);
            }
            var javaCheck = JavaResolver.resolve(meta.gameVersion, javaHome);
            if (!javaCheck.ok()) Output.warn(javaCheck.message());
            else if (javaCheck.message() != null) Output.info(javaCheck.message());
            return 0;
        }
    }

    @Command(name = "start", description = "启动实例（默认前台；-d 后台监督进程）")
    static final class Start implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Option(names = {"-d", "--detach"}, description = "后台运行（独立监督进程）")
        boolean detach;

        @Override
        public Integer call() throws Exception {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(name);
            var javaCheck = JavaResolver.resolve(status.meta().gameVersion, status.meta().javaHome);
            if (!javaCheck.ok()) Output.warn(javaCheck.message());
            if (status.state().isLive()) {
                throw new CeobeException("实例 %s 已在运行（%s）".formatted(name, status.state().zhLabel()));
            }
            if (!detach) {
                return Supervisor.run(name, true);
            }
            startDetached(manager, name);
            reportRunning(name, manager);
            return 0;
        }

        /** 后台拉起监督进程并等待服务端就绪（容忍通道尚未建立的窗口期）。 */
        static void startDetached(InstanceManager manager, String name) throws Exception {
            Path dir = manager.dir(name);
            // 清理上一轮遗留的控制通道信息，避免读到已失效端口
            InstanceControl.clear(dir);
            spawnSupervisor(dir, name);
            long deadline = System.currentTimeMillis() + 300_000;
            while (System.currentTimeMillis() < deadline) {
                try {
                    var state = InstanceControl.state(dir);
                    if (state.isPresent() && state.get() == InstanceState.RUNNING) return;
                } catch (CeobeException e) {
                    // 通道尚未就绪（拒绝连接 / 超时）——继续等待
                }
                Thread.sleep(1000);
            }
            throw new CeobeException("实例 %s 启动超时，请查看 logs/supervisor.log".formatted(name));
        }

        static void reportRunning(String name, InstanceManager manager) {
            String address = "127.0.0.1:" + manager.meta(name).port;
            if (Output.json) {
                Output.json(Output.map("name", name, "state", "RUNNING", "address", address));
            } else {
                Output.info("实例 %s 已就绪 ✓  本机地址: %s", name, address);
                Output.info("开启玩家通道: ceobe tunnel up %s --kind pinggy", name);
            }
        }

        static void spawnSupervisor(Path instanceDir, String name) throws IOException {
            String java = JavaEnv.javaLauncher()
                    .orElseThrow(() -> new CeobeException(
                            "找不到可用的 Java 运行时（未设置 JAVA_HOME 且当前镜像未内置 bin/java）"))
                    .toString();
            String classpath = selfClasspath();
            String home = System.getProperty("ceobe.home");
            List<String> cmd = new ArrayList<>(List.of(java));
            cmd.add("-Dfile.encoding=UTF-8");
            if (home != null) cmd.add("-Dceobe.home=" + home);
            cmd.add("-cp");
            cmd.add(classpath);
            cmd.add(Supervisor.class.getName());
            cmd.add(name);

            ProcessBuilder pb = new ProcessBuilder(cmd).directory(instanceDir.toFile());
            pb.redirectOutput(ProcessBuilder.Redirect.appendTo(
                    instanceDir.resolve("supervisor.log").toFile()));
            pb.redirectErrorStream(true);
            pb.start();
        }

        /** 定位 ceobe 自身 jar/classes 目录：{@code java -jar} 下 java.class.path 不可靠。 */
        static String selfClasspath() {
            try {
                var cs = CeobeCli.class.getProtectionDomain().getCodeSource();
                if (cs != null) {
                    return Path.of(cs.getLocation().toURI()).toString();
                }
            } catch (Exception ignored) {
                // 回退到 java.class.path
            }
            return System.getProperty("java.class.path", "");
        }
    }

    @Command(name = "stop", description = "优雅停止实例")
    static final class Stop implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Option(names = "--force", description = "跳过优雅停止，直接终止")
        boolean force;

        @Override
        public Integer call() throws Exception {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(name);
            if (!status.state().isLive()) {
                Output.info("实例 %s 未在运行", name);
                return 0;
            }
            Path dir = manager.dir(name);
            if (!InstanceControl.isAvailable(dir)) {
                throw new CeobeException(
                        "实例 %s 由不受控进程托管（无控制通道）；可用任务管理器结束 pid %d"
                                .formatted(name, status.pid()));
            }
            if (force) {
                InstanceControl.kill(dir);
            } else {
                InstanceControl.stop(dir);
            }
            long deadline = System.currentTimeMillis() + 120_000;
            while (System.currentTimeMillis() < deadline) {
                var state = InstanceControl.state(dir);
                if (state.isEmpty() || state.get() == InstanceState.STOPPED
                        || state.get() == InstanceState.CRASHED) {
                    Output.info("实例 %s 已停止", name);
                    return 0;
                }
                Thread.sleep(700);
            }
            throw new CeobeException("停止超时：实例 %s 仍在运行".formatted(name));
        }
    }

    @Command(name = "restart", description = "重启实例（后台监督进程）")
    static final class Restart implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Override
        public Integer call() throws Exception {
            InstanceManager manager = new InstanceManager();
            InstanceStatus status = manager.require(name);
            Path dir = manager.dir(name);
            if (status.state().isLive() && InstanceControl.isAvailable(dir)) {
                InstanceControl.stop(dir);
                long deadline = System.currentTimeMillis() + 120_000;
                while (System.currentTimeMillis() < deadline
                        && InstanceControl.state(dir).map(InstanceState::isLive).orElse(false)) {
                    Thread.sleep(700);
                }
            }
            Start.startDetached(manager, name);
            Start.reportRunning(name, manager);
            return 0;
        }
    }

    @Command(name = "rm", aliases = {"remove", "delete"}, description = "删除实例")
    static final class Rm implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Option(names = "--purge", description = "连同世界数据一并删除（默认仅移除注册）")
        boolean purge;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            manager.remove(name, purge);
            Output.info("实例 %s 已删除%s", name, purge ? "（含数据）" : "（世界数据保留在目录中）");
            return 0;
        }
    }

    @Command(name = "logs", description = "查看实例日志（-f 跟随）")
    static final class Logs implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Option(names = {"-f", "--follow"}, description = "持续跟随输出")
        boolean follow;

        @Option(names = "--tail", defaultValue = "100", description = "初始显示行数")
        int tail;

        @Override
        public Integer call() throws Exception {
            InstanceManager manager = new InstanceManager();
            Path dir = manager.dir(name);
            manager.require(name);
            Path logFile = dir.resolve("console.log");

            List<String> initial = InstanceControl.isAvailable(dir)
                    ? InstanceControl.logs(dir, tail)
                    : readTail(logFile, tail);
            initial.forEach(System.out::println);
            if (!follow) return 0;

            long position = Files.isRegularFile(logFile) ? Files.size(logFile) : 0;
            while (true) {
                Thread.sleep(400);
                if (Files.isRegularFile(logFile) && Files.size(logFile) > position) {
                    try (var channel = java.nio.channels.FileChannel.open(logFile)) {
                        channel.position(position);
                        try (var reader = new java.io.BufferedReader(
                                java.nio.channels.Channels.newReader(channel, StandardCharsets.UTF_8))) {
                            String line;
                            while ((line = reader.readLine()) != null) System.out.println(line);
                        }
                    }
                    position = Files.size(logFile);
                }
                boolean live = InstanceControl.state(dir)
                        .map(InstanceState::isLive)
                        .orElseGet(() -> manager.find(name)
                                .map(s -> s.state().isLive()).orElse(false));
                if (!live) {
                    Output.info("—— 实例已停止，跟随结束 ——");
                    return 0;
                }
            }
        }

        private static List<String> readTail(Path file, int tail) {
            try {
                if (!Files.isRegularFile(file)) return List.of();
                List<String> all = Files.readAllLines(file, StandardCharsets.UTF_8);
                return all.subList(Math.max(0, all.size() - tail), all.size());
            } catch (IOException e) {
                return List.of();
            }
        }
    }

    @Command(name = "exec", description = "向实例控制台发送命令")
    static final class Exec implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Parameters(index = "1..*", paramLabel = "<命令…>", description = "服务端命令，如 say 你好")
        List<String> command;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            Path dir = manager.dir(name);
            manager.require(name);
            String joined = String.join(" ", command);
            if (!InstanceControl.isAvailable(dir)) {
                throw new CeobeException("实例 %s 未在受控进程中运行，无法发送命令".formatted(name));
            }
            InstanceControl.exec(dir, joined);
            if (Output.json) {
                Output.json(Output.map("name", name, "command", joined, "sent", true));
            } else {
                Output.info("已发送: %s", joined);
            }
            return 0;
        }
    }

    @Command(name = "update", description = "修改实例设置（名称/内存/端口/JVM 参数/authlib-injector）")
    static final class Update implements Callable<Integer> {
        @Parameters(index = "0", paramLabel = "<名称>") String name;

        @Option(names = "--name", paramLabel = "新名", description = "重命名实例")
        String newName;

        @Option(names = "--mem", paramLabel = "SIZE", description = "内存上限，如 4G")
        String mem;

        @Option(names = "--port", paramLabel = "PORT", description = "端口或 auto")
        String port;

        @Option(names = "--jvm", paramLabel = "ARGS", description = "附加 JVM 参数（传空串可清空）")
        String jvm;

        @Option(names = "--authlib", paramLabel = "URL", description = "authlib-injector Yggdrasil 地址（传空串关闭）")
        String authlib;

        @Override
        public Integer call() {
            InstanceManager manager = new InstanceManager();
            manager.require(name);
            String target = name;
            if (newName != null && !newName.isBlank() && !newName.equals(name)) {
                manager.rename(name, newName);
                target = newName;
            }
            Integer memMb = mem == null ? null : parseMemory(mem);
            Integer portNum = port == null ? null
                    : (port.equalsIgnoreCase("auto") ? manager.suggestFreePort(25565) : parsePort(port));
            if (memMb == null && portNum == null && jvm == null && authlib == null) {
                Output.info("未指定任何改动（--name/--mem/--port/--jvm/--authlib）");
                return 0;
            }
            manager.updateInstance(target, memMb, portNum, jvm, authlib);
            if (Output.json) {
                Output.json(Output.map("name", target, "memoryMb", memMb, "port", portNum,
                        "jvmArgs", jvm, "authlibInjector", authlib));
            } else {
                Output.info("实例 %s 已更新", target);
            }
            return 0;
        }
    }

    static ServerType parseType(String raw) {
        try {
            return ServerType.valueOf(raw.trim().toUpperCase());
        } catch (IllegalArgumentException e) {
            throw new CeobeException("未知服务端类型: %s（可选 vanilla/paper/fabric/neoforge/purpur）".formatted(raw));
        }
    }

    /** 解析为 1-65535 的整数（不接受 auto）。 */
    static int parsePort(String raw) {
        try {
            int p = Integer.parseInt(raw.trim());
            if (p < 1 || p > 65535) throw new NumberFormatException();
            return p;
        } catch (NumberFormatException e) {
            throw new CeobeException("端口格式错误: %s（应为 1-65535 的整数或 auto）".formatted(raw));
        }
    }

    /** "auto"/空 → 自动选取空闲端口；否则解析为 1-65535 的整数。 */
    static int resolvePort(String raw, InstanceManager manager) {
        String s = raw == null ? "" : raw.trim();
        if (s.isEmpty() || s.equalsIgnoreCase("auto")) {
            return manager.suggestFreePort(25565);
        }
        try {
            int p = Integer.parseInt(s);
            if (p < 1 || p > 65535) throw new NumberFormatException();
            return p;
        } catch (NumberFormatException e) {
            throw new CeobeException("端口格式错误: %s（应为 1-65535 的整数或 auto）".formatted(raw));
        }
    }

    /** "4G" → 4096；"4096M"/"4096" → 4096。 */
    static int parseMemory(String raw) {
        String s = raw.trim().toUpperCase();
        try {
            if (s.endsWith("G")) return Integer.parseInt(s.substring(0, s.length() - 1)) * 1024;
            if (s.endsWith("M")) return Integer.parseInt(s.substring(0, s.length() - 1));
            return Integer.parseInt(s);
        } catch (NumberFormatException e) {
            throw new CeobeException("内存格式错误: %s（示例 4G / 4096M）".formatted(raw));
        }
    }

    private InstanceCommands() {
    }
}
