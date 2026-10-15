package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.event.InstanceEvent;
import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.runtime.ControlServer;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.ServerProcess;
import im.xrl.ceobe.core.tunnel.TunnelManager;
import im.xrl.ceobe.core.webconsole.WebConsole;

import java.nio.file.Path;
import java.util.concurrent.CountDownLatch;

/**
 * 实例监督进程：托管一个 MC 服务端 + 开放控制通道。
 * <p>{@code ceobe start -d} 会以独立 JVM 拉起本类；前台模式则由 start 直接调用
 * {@link #run}。
 * <p>用法：{@code java -cp ceobe.jar im.xrl.ceobe.cli.Supervisor <实例名> [--foreground]}
 */
public final class Supervisor {

    public static void main(String[] args) throws Exception {
        String name = args.length > 0 ? args[0] : null;
        if (name == null) {
            System.err.println("用法: Supervisor <实例名> [--foreground]");
            System.exit(2);
        }
        boolean foreground = args.length > 1 && args[1].equals("--foreground");
        run(name, foreground);
    }

    /** 阻塞运行直至实例停止；返回进程退出码。 */
    public static int run(String name, boolean foreground) throws InterruptedException {
        InstanceManager manager = new InstanceManager();
        var meta = manager.meta(name);
        Path dir = manager.dir(name);
        ServerProcess process = new ServerProcess(meta, dir);
        TunnelManager tunnels = new TunnelManager(manager);
        CountDownLatch finished = new CountDownLatch(1);

        process.addListener(event -> {
            if (foreground) {
                switch (event) {
                    case InstanceEvent.LogLine l -> System.out.println(l.line());
                    case InstanceEvent.StateChanged s -> System.out.println(
                            "[ceobe] " + name + " -> " + s.state().zhLabel());
                    case InstanceEvent.TunnelChanged t -> System.out.println(
                            "[ceobe] " + t.kind() + " 地址: " + t.publicUrl());
                }
            }
            if (event instanceof InstanceEvent.StateChanged s
                    && (s.state() == InstanceState.STOPPED || s.state() == InstanceState.CRASHED)) {
                finished.countDown();
            }
        });

        try (ControlServer control = new ControlServer(process, dir, name, tunnels);
             WebConsole web = new WebConsole(manager, 0)) {
            tunnels.webConsolePort(web.port());
            if (foreground) {
                System.out.println("[ceobe] Web 控制台: http://127.0.0.1:" + web.port());
            }
            Runtime.getRuntime().addShutdownHook(new Thread(() -> {
                if (process.isAlive()) {
                    if (foreground) System.out.println("[ceobe] 收到中断，正在优雅停止…");
                    process.stop(java.time.Duration.ofSeconds(90));
                }
            }, "ceobe-shutdown"));
            process.start();
            finished.await();
        } finally {
            tunnels.close();
        }
        return 0;
    }

    private Supervisor() {
    }
}
