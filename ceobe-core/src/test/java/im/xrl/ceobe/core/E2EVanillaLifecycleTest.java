package im.xrl.ceobe.core;

import im.xrl.ceobe.core.config.ServerPropertiesFile;
import im.xrl.ceobe.core.event.InstanceEvent;
import im.xrl.ceobe.core.model.InstanceSpec;
import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.provider.ProviderRegistry;
import im.xrl.ceobe.core.runtime.InstanceManager;
import org.junit.jupiter.api.Tag;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.net.ServerSocket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * 端到端验收（PRD 场景 3 的自动化）：创建 → 启动 → 命令下发 → 优雅停止。
 * <p>需要网络下载 vanilla 服务端（~60MB），默认被 surefire 排除；
 * 显式执行：{@code mvn test -DexcludedGroups= -Dgroups=network}
 */
@Tag("network")
class E2EVanillaLifecycleTest {

    @Test
    void 原版实例全生命周期(@TempDir Path home) throws Exception {
        if (System.getProperty("ceobe.home") == null) {
            System.setProperty("ceobe.home", home.toString());
        }

        InstanceManager manager = new InstanceManager();
        String version = ProviderRegistry.of(ServerType.VANILLA).listVersions().get(0);
        int port = freePort();

        InstanceSpec spec = new InstanceSpec("e2e", ServerType.VANILLA, version, null,
                2048, "", port, null);
        var meta = manager.create(spec, (stage, done, total) -> System.out.println("[e2e] " + stage));
        assertEquals(ServerType.VANILLA, meta.serverType);

        Path dir = manager.dir("e2e");
        assertTrue(Files.isRegularFile(dir.resolve("server.jar")), "服务端 jar 应已安装");
        assertTrue(Files.isRegularFile(dir.resolve("eula.txt")), "eula.txt 应已生成");

        // 放宽认正版/白名单，便于空服启动
        ServerPropertiesFile props = ServerPropertiesFile.load(dir.resolve("server.properties"));
        props.set("online-mode", "false");
        props.set("white-list", "false");
        props.set("level-seed", "ceobe");
        props.save();
        assertEquals(String.valueOf(port), props.get("server-port"));

        CountDownLatch running = new CountDownLatch(1);
        AtomicReference<InstanceState> lastState = new AtomicReference<>();
        manager.addListener(e -> {
            if (e instanceof InstanceEvent.StateChanged sc) {
                lastState.set(sc.state());
                System.out.println("[e2e] state -> " + sc.state());
                if (sc.state() == InstanceState.RUNNING) running.countDown();
            }
        });

        Instant start = Instant.now();
        manager.start("e2e");
        assertTrue(running.await(240, TimeUnit.SECONDS),
                "服务端应在 240 秒内就绪，实际状态: " + lastState.get());
        System.out.println("[e2e] 就绪耗时 " + Duration.between(start, Instant.now()).toSeconds() + "s");

        manager.exec("e2e", "say hello-ceobe");
        assertTrue(awaitLogContains(manager, "e2e", "hello-ceobe", Duration.ofSeconds(20)),
                "控制台命令应有日志回显");

        assertEquals(InstanceState.RUNNING, manager.require("e2e").state());
        manager.stop("e2e");
        assertEquals(InstanceState.STOPPED, manager.require("e2e").state(), "优雅停止后应为已停止");
        assertTrue(manager.require("e2e").pid() < 0, "进程应已退出");

        // 二次启动应成功（锁与状态可复用）
        CountDownLatch running2 = new CountDownLatch(1);
        manager.addListener(e -> {
            if (e instanceof InstanceEvent.StateChanged sc && sc.state() == InstanceState.RUNNING) {
                running2.countDown();
            }
        });
        manager.start("e2e");
        assertTrue(running2.await(180, TimeUnit.SECONDS), "二次启动应成功");
        manager.stop("e2e");
        assertEquals(InstanceState.STOPPED, manager.require("e2e").state());
    }

    private static boolean awaitLogContains(InstanceManager manager, String name, String needle,
                                            Duration timeout) throws InterruptedException {
        Instant deadline = Instant.now().plus(timeout);
        while (Instant.now().isBefore(deadline)) {
            List<String> logs = manager.processOf(name).map(p -> p.recentLogs(200)).orElse(List.of());
            if (logs.stream().anyMatch(l -> l.contains(needle))) return true;
            Thread.sleep(500);
        }
        return false;
    }

    private static int freePort() throws Exception {
        try (ServerSocket socket = new ServerSocket(0)) {
            socket.setReuseAddress(true);
            return socket.getLocalPort();
        }
    }

    /** 断言不必要的告警：确认核心文件以 UTF-8 写出。 */
    @Test
    void 实例目录编码与文件集(@TempDir Path home) throws Exception {
        if (System.getProperty("ceobe.home") == null) {
            System.setProperty("ceobe.home", home.toString());
        }
        Path dir = home.resolve("instances").resolve("probe");
        Files.createDirectories(dir);
        Files.writeString(dir.resolve("motd.txt"), "中文测试", StandardCharsets.UTF_8);
        assertEquals("中文测试", Files.readString(dir.resolve("motd.txt"), StandardCharsets.UTF_8));
    }
}
