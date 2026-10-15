package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.config.ServerPropertiesFile;
import im.xrl.ceobe.core.event.InstanceEvent;
import im.xrl.ceobe.core.event.InstanceListener;
import im.xrl.ceobe.core.jdk.JavaResolver;
import im.xrl.ceobe.core.model.InstanceMeta;
import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.model.InstanceSpec;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.provider.InstalledServer;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.provider.ProviderRegistry;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.CeobeException;
import im.xrl.ceobe.core.util.Json;
import im.xrl.ceobe.core.util.Ports;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;

/**
 * 实例管理器：CRUD + 生命周期 + 跨进程互斥。
 * <p>状态存储：{@code ~/.ceobe/instances/<name>/}（instance.json / .lock / .pid / .state）。
 * <p>本进程托管的实例持有 {@link ServerProcess}；其他进程通过 .state/.pid 推断。
 */
public final class InstanceManager {

    private static final Duration STOP_GRACE = Duration.ofSeconds(90);

    private final Map<String, ServerProcess> hosted = new ConcurrentHashMap<>();
    private final List<InstanceListener> listeners = new CopyOnWriteArrayList<>();

    public void addListener(InstanceListener l) {
        listeners.add(l);
        hosted.values().forEach(p -> p.addListener(l));
    }

    // —— 查询 ——

    public List<InstanceStatus> list() {
        List<InstanceStatus> out = new ArrayList<>();
        for (InstanceMeta meta : loadAllMetas()) {
            out.add(statusOf(meta));
        }
        out.sort(Comparator.comparing(s -> s.meta().name));
        return out;
    }

    public Optional<InstanceStatus> find(String name) {
        Path dir = CeobeHome.instanceDir(name);
        if (!Files.isRegularFile(dir.resolve("instance.json"))) return Optional.empty();
        return Optional.of(statusOf(readMeta(dir)));
    }

    public InstanceStatus require(String name) {
        return find(name).orElseThrow(() -> CeobeException.of("实例不存在: %s", name));
    }

    public InstanceMeta meta(String name) {
        return require(name).meta();
    }

    public Path dir(String name) {
        return CeobeHome.instanceDir(name);
    }

    // —— 端口排他性 ——

    /**
     * 校验端口在实例间排他：不得与其它实例已登记的端口相同。
     * <p>穿透通道直接指向该端口，若两个实例共用端口，玩家的地址会连到错误的服务端。
     */
    public void assertPortExclusive(String self, int port) {
        for (InstanceMeta m : loadAllMetas()) {
            if (!m.name.equals(self) && m.port == port) {
                throw CeobeException.of(
                        "端口 %d 已被实例 %s 占用；端口需在实例间排他。可改用 `--port auto` 自动选取空闲端口",
                        port, m.name);
            }
        }
    }

    /** 从 {@code from} 起找一个未被任何实例登记、且本机可独占绑定的空闲端口。 */
    public int suggestFreePort(int from) {
        java.util.Set<Integer> taken = new java.util.HashSet<>();
        for (InstanceMeta m : loadAllMetas()) taken.add(m.port);
        for (int p = Math.max(1, from); p <= 65535; p++) {
            if (!taken.contains(p) && Ports.isFree(p)) return p;
        }
        throw CeobeException.of("从端口 %d 起找不到可用端口", from);
    }

    private InstanceStatus statusOf(InstanceMeta meta) {
        Path dir = CeobeHome.instanceDir(meta.name);
        ServerProcess hostedProcess = hosted.get(meta.name);
        if (hostedProcess != null && hostedProcess.isAlive()) {
            return new InstanceStatus(meta, hostedProcess.state(),
                    hostedProcess.isAlive() ? pidOf(dir) : -1, true,
                    meta.lastPinggyAddress, meta.lastCloudflareUrl);
        }
        long pid = pidOf(dir);
        InstanceState state = readStateFile(dir);
        if (state.isLive() && pid > 0 && ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false)) {
            return new InstanceStatus(meta, state, pid, false,
                    meta.lastPinggyAddress, meta.lastCloudflareUrl);
        }
        if (state.isLive()) {
            // 残留状态（进程已死）——修正为停止
            state = InstanceState.STOPPED;
        }
        return new InstanceStatus(meta, state, -1, false, meta.lastPinggyAddress, meta.lastCloudflareUrl);
    }

    /** 更新实例使用的 javaHome（null = 继承当前 JVM）。 */
    public void setJavaHome(String name, String javaHome) {
        require(name);
        Path dir = CeobeHome.instanceDir(name);
        InstanceMeta meta = readMeta(dir);
        meta.javaHome = javaHome;
        saveMeta(dir, meta);
    }

    // —— 创建/删除 ——

    public InstanceMeta create(InstanceSpec spec, ProgressListener progress) {
        Path dir = CeobeHome.instanceDir(spec.name());
        if (Files.exists(dir.resolve("instance.json"))) {
            throw CeobeException.of("实例已存在: %s", spec.name());
        }
        // 端口排他性：先于任何下载/落盘校验，避免冲突时才失败
        assertPortExclusive(spec.name(), spec.port());
        if (!Ports.isFree(spec.port())) {
            throw CeobeException.of(
                    "端口 %d 当前已被本机其它程序占用；可改用 `--port auto` 自动选取空闲端口", spec.port());
        }
        CeobeHome.ensure(dir);
        // 先按最终参数生成基础文件，再安装服务端
        try {
            Files.writeString(dir.resolve("eula.txt"), "eula=true\n", StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new CeobeException("写入 eula.txt 失败", e);
        }
        writeDefaultProperties(dir, spec.port());

        progress.update("安装服务端 " + spec.serverType().name() + " " + spec.gameVersion(), 0, -1);
        InstalledServer installed = ProviderRegistry.of(spec.serverType())
                .install(dir, spec.gameVersion(), spec.loaderVersion(), progress);

        InstanceMeta meta = new InstanceMeta(spec.name(), spec.serverType(),
                installed.gameVersion(), installed.loaderVersion());
        meta.memoryMb = spec.memoryMb();
        meta.jvmArgs = spec.jvmArgs() == null ? "" : spec.jvmArgs();
        meta.port = spec.port();
        // 解析应使用的 Java：显式指定 > 自管 JDK 命中 > 继承当前 JVM
        var java = JavaResolver.resolve(installed.gameVersion(), spec.javaHome());
        meta.javaHome = java.javaHome() == null ? null : java.javaHome().toString();
        meta.launchJar = installed.launchJar();
        meta.launchArgsFile = installed.launchArgsFile();
        saveMeta(dir, meta);
        progress.update("创建完成", 1, 1);
        return meta;
    }

    private static void writeDefaultProperties(Path dir, int port) {
        Path props = dir.resolve("server.properties");
        if (Files.isRegularFile(props)) return;
        ServerPropertiesFile file = ServerPropertiesFile.load(props);
        // 用 template 起底：直接写文本再加载改端口
        try {
            Files.writeString(props, ServerPropertiesFile.template(), StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new CeobeException("生成 server.properties 失败", e);
        }
        ServerPropertiesFile f2 = ServerPropertiesFile.load(props);
        f2.set("server-port", String.valueOf(port));
        f2.set("query.port", String.valueOf(port));
        f2.save();
    }

    public void remove(String name, boolean purge) {
        InstanceStatus s = require(name);
        if (s.state().isLive()) {
            throw CeobeException.of("实例 %s 仍在运行，请先停止", name);
        }
        Path dir = CeobeHome.instanceDir(name);
        try {
            if (purge) {
                deleteRecursively(dir);
            } else {
                Files.deleteIfExists(dir.resolve("instance.json"));
                Files.deleteIfExists(dir.resolve(".state"));
                Files.deleteIfExists(dir.resolve(".pid"));
                Files.deleteIfExists(dir.resolve(".lock"));
            }
        } catch (IOException e) {
            throw new CeobeException("删除实例失败: " + e.getMessage(), e);
        }
        hosted.remove(name);
    }

    // —— 生命周期 ——

    public void start(String name) {
        InstanceStatus s = require(name);
        assertPortExclusive(name, s.meta().port);
        if (!hosted.containsKey(name)) {
            verifyNotRunningElsewhere(name, s);
        }
        ServerProcess process = hosted.computeIfAbsent(name, n -> {
            ServerProcess p = new ServerProcess(s.meta(), CeobeHome.instanceDir(n));
            listeners.forEach(p::addListener);
            return p;
        });
        if (process.isAlive()) throw CeobeException.of("实例 %s 已在运行", name);
        String launch = s.meta().launchArgsFile != null && !s.meta().launchArgsFile.isBlank()
                ? s.meta().launchArgsFile : s.meta().launchJar;
        if (!Files.isRegularFile(dir(name).resolve(launch))) {
            throw CeobeException.of("启动器 %s 不存在，实例可能未安装完整", launch);
        }
        process.start();
    }

    private void verifyNotRunningElsewhere(String name, InstanceStatus s) {
        if (s.state().isLive() && s.pid() > 0) {
            throw CeobeException.of(
                    "实例 %s 已被其他进程托管（pid %d），请先在那里停止，或用 `ceobe stop %s` 后再试",
                    name, s.pid(), name);
        }
    }

    public void stop(String name) {
        require(name);
        ServerProcess p = hosted.get(name);
        if (p != null && p.isAlive()) {
            p.stop(STOP_GRACE);
            return;
        }
        // 非本进程托管：读取 .pid 请求外部停止
        long pid = pidOf(CeobeHome.instanceDir(name));
        if (pid > 0 && ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false)) {
            throw CeobeException.of(
                    "实例 %s 由其他进程托管（pid %d），无法在此处停止", name, pid);
        }
        throw CeobeException.of("实例 %s 未在运行", name);
    }

    public void kill(String name) {
        ServerProcess p = hosted.get(name);
        if (p != null) p.kill();
    }

    public void restart(String name) {
        stop(name);
        start(name);
    }

    public void exec(String name, String command) {
        ServerProcess p = hosted.get(name);
        if (p == null || !p.isAlive()) {
            throw CeobeException.of("实例 %s 未在本进程运行，无法发送命令", name);
        }
        p.sendCommand(command);
    }

    /** 本进程托管的实例（GUI 可订阅其日志）；未托管返回空。 */
    public Optional<ServerProcess> processOf(String name) {
        return Optional.ofNullable(hosted.get(name));
    }

    /** 记录/刷新某通道的公网地址，并广播事件（Pinggy 续期后会再次调用）。 */
    public void rememberTunnel(String name, String kind, String url) {
        Path dir = CeobeHome.instanceDir(name);
        InstanceMeta meta = readMeta(dir);
        if ("pinggy".equals(kind)) meta.lastPinggyAddress = url;
        if ("cloudflare".equals(kind)) meta.lastCloudflareUrl = url;
        saveMeta(dir, meta);
        listeners.forEach(l -> l.onEvent(new InstanceEvent.TunnelChanged(name, kind, url)));
    }

    /** 注入隧道诊断日志到实例事件流（GUI 控制台可见）。 */
    public void tunnelLog(String name, String line) {
        listeners.forEach(l -> l.onEvent(new InstanceEvent.LogLine(name, line)));
    }

    // —— 元数据 IO ——

    private List<InstanceMeta> loadAllMetas() {
        List<InstanceMeta> out = new ArrayList<>();
        Path base = CeobeHome.instancesDir();
        try (var stream = Files.list(base)) {
            stream.filter(Files::isDirectory)
                    .filter(d -> Files.isRegularFile(d.resolve("instance.json")))
                    .forEach(d -> out.add(readMeta(d)));
        } catch (IOException e) {
            return out;
        }
        return out;
    }

    private static InstanceMeta readMeta(Path dir) {
        try {
            return Json.parse(Files.readString(dir.resolve("instance.json"), StandardCharsets.UTF_8),
                    InstanceMeta.class);
        } catch (IOException e) {
            throw new CeobeException("读取实例元数据失败: " + dir, e);
        }
    }

    private static void saveMeta(Path dir, InstanceMeta meta) {
        try {
            Files.writeString(dir.resolve("instance.json"), Json.write(meta), StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new CeobeException("写入实例元数据失败: " + dir, e);
        }
    }

    private static long pidOf(Path dir) {
        try {
            Path pid = dir.resolve(".pid");
            if (!Files.isRegularFile(pid)) return -1;
            return Long.parseLong(Files.readString(pid).trim());
        } catch (IOException | NumberFormatException e) {
            return -1;
        }
    }

    private static InstanceState readStateFile(Path dir) {
        try {
            Path f = dir.resolve(".state");
            if (!Files.isRegularFile(f)) return InstanceState.CREATED;
            return InstanceState.valueOf(Files.readString(f).trim());
        } catch (IOException | IllegalArgumentException e) {
            return InstanceState.CREATED;
        }
    }

    private static void deleteRecursively(Path root) throws IOException {
        if (!Files.exists(root)) return;
        try (var walk = Files.walk(root)) {
            walk.sorted(Comparator.reverseOrder()).forEach(p -> {
                try {
                    Files.deleteIfExists(p);
                } catch (IOException e) {
                    throw new CeobeException("删除失败: " + p, e);
                }
            });
        }
    }

}
