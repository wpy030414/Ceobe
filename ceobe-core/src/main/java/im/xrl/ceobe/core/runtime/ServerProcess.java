package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.event.InstanceEvent;
import im.xrl.ceobe.core.event.InstanceListener;
import im.xrl.ceobe.core.jdk.JavaEnv;
import im.xrl.ceobe.core.model.InstanceMeta;
import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.util.CeobeException;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Duration;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;

/**
 * 单个 MC 服务端进程的托管：启动、日志泵、状态机、控制台 stdin、优雅停止。
 * <p>状态推进依据：日志出现 {@code Done (…s)! For help} → RUNNING。
 */
public final class ServerProcess {

    private static final Pattern DONE = Pattern.compile("Done \\([0-9.]+s\\)! For help");
    private static final int RING_SIZE = 800;

    private final InstanceMeta meta;
    private final Path dir;
    private final List<InstanceListener> listeners = new CopyOnWriteArrayList<>();
    private final Deque<String> ring = new ArrayDeque<>(RING_SIZE);

    private volatile Process process;
    private volatile InstanceState state = InstanceState.STOPPED;
    private volatile boolean stopRequested;
    private BufferedWriter stdin;

    public ServerProcess(InstanceMeta meta, Path dir) {
        this.meta = meta;
        this.dir = dir;
    }

    public void addListener(InstanceListener l) {
        listeners.add(l);
    }

    public InstanceState state() {
        return state;
    }

    public boolean isAlive() {
        Process p = process;
        return p != null && p.isAlive();
    }

    public synchronized List<String> recentLogs(int n) {
        List<String> all = new ArrayList<>(ring);
        int from = Math.max(0, all.size() - n);
        return all.subList(from, all.size());
    }

    public synchronized void start() {
        if (isAlive()) throw new CeobeException("实例已在运行");
        checkPortFree(meta.port);

        Path authlib = null;
        if (meta.authlibInjectorUrl != null && !meta.authlibInjectorUrl.isBlank()) {
            appendLog("[ceobe] 准备 authlib-injector（三方鉴权）…");
            authlib = AuthlibInjector.ensure((stage, d, t) -> {
            });
            appendLog("[ceobe] 已启用 authlib-injector: " + meta.authlibInjectorUrl);
        }
        List<String> cmd = buildCommand(authlib);
        // 落盘启动命令行：便于事后审计内存/GC/额外 JVM 参数
        appendLog("$ " + String.join(" ", cmd));

        ProcessBuilder pb = new ProcessBuilder(cmd).directory(dir.toFile());
        pb.redirectErrorStream(true);
        try {
            process = pb.start();
        } catch (IOException e) {
            throw new CeobeException("启动失败: " + e.getMessage(), e);
        }
        stdin = new BufferedWriter(new OutputStreamWriter(process.getOutputStream(), StandardCharsets.UTF_8));
        writeRuntimeFiles();
        transition(InstanceState.STARTING, 0);

        Thread.ofVirtual().name("ceobe-log-" + meta.name).start(this::pump);
        Thread.ofVirtual().name("ceobe-watch-" + meta.name).start(this::watchdog);
    }

    private List<String> buildCommand(Path authlib) {
        List<String> cmd = new ArrayList<>();
        cmd.add(javaLauncher());
        cmd.add("-Xms" + Math.max(256, meta.memoryMb / 2) + "M");
        cmd.add("-Xmx" + meta.memoryMb + "M");
        cmd.add("-XX:+UseG1GC");
        cmd.add("-XX:+ParallelRefProcEnabled");
        cmd.add("-XX:MaxGCPauseMillis=200");
        cmd.add("-Dfile.encoding=UTF-8");
        if (meta.jvmArgs != null && !meta.jvmArgs.isBlank()) {
            for (String a : meta.jvmArgs.trim().split("\\s+")) {
                if (!a.isBlank()) cmd.add(a);
            }
        }
        if (authlib != null) {
            cmd.add("-javaagent:" + authlib.toAbsolutePath() + "=" + meta.authlibInjectorUrl);
        }
        if (meta.launchArgsFile != null && !meta.launchArgsFile.isBlank()) {
            // NeoForge 等：@参数文件启动（内含 -classpath / 主类 / --fml.* 参数）
            cmd.add("@" + meta.launchArgsFile);
        } else {
            cmd.add("-jar");
            cmd.add(meta.launchJar);
        }
        cmd.add("nogui");
        return cmd;
    }

    /**
     * 定位用于拉起服务端的 java 启动器。
     * <p>注意：不可盲目回退到 {@code java.home}——jpackage 打包的 GUI 其 runtime 镜像被剥离了
     * {@code bin/java}（GUI 由原生 launcher 直连 jvm.dll），用它 fork 子进程会静默失败。
     */
    private String javaLauncher() {
        if (meta.javaHome != null && !meta.javaHome.isBlank()) {
            Path launcher = Path.of(meta.javaHome, "bin", "java" + exe());
            if (!Files.isRegularFile(launcher)) {
                throw new CeobeException(
                        "实例指定的 Java 运行时缺少启动器: " + launcher + "；请在实例设置中重新指定 JRE");
            }
            return launcher.toString();
        }
        return JavaEnv.javaLauncher()
                .orElseThrow(() -> new CeobeException(
                        "找不到可用的 Java 运行时（当前镜像未内置 bin/java，且未设置 JAVA_HOME）；"
                                + "请先安装 JDK，再在实例设置中指定 JRE"))
                .toString();
    }

    private static String exe() {
        return System.getProperty("os.name", "").toLowerCase().contains("win") ? ".exe" : "";
    }

    private void pump() {
        Process p = process;
        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                appendLog(line);
                if (state == InstanceState.STARTING && DONE.matcher(line).find()) {
                    transition(InstanceState.RUNNING, 0);
                }
            }
        } catch (IOException ignored) {
            // 进程结束后流关闭属正常
        }
    }

    private void watchdog() {
        Process p = process;
        int code = -1;
        try {
            code = p.waitFor();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        InstanceState finalState = stopRequested ? InstanceState.STOPPED
                : (code == 0 ? InstanceState.STOPPED : InstanceState.CRASHED);
        process = null;
        stdin = null;
        transition(finalState, code);
    }

    public void sendCommand(String command) {
        BufferedWriter w = stdin;
        if (w == null) throw new CeobeException("实例未运行，无法发送命令");
        try {
            w.write(command);
            w.newLine();
            w.flush();
            appendLog("> " + command);
        } catch (IOException e) {
            throw new CeobeException("命令发送失败: " + e.getMessage(), e);
        }
    }

    /** 优雅停止：stdin 写 stop，宽限期内等待，超时强杀。 */
    public void stop(Duration grace) {
        if (!isAlive()) {
            transition(InstanceState.STOPPED, 0);
            return;
        }
        stopRequested = true;
        transition(InstanceState.STOPPING, 0);
        try {
            sendCommand("stop");
        } catch (CeobeException ignored) {
            // stdin 可能已关闭，直接走强杀
        }
        Process p = process;
        try {
            if (!p.waitFor(grace.toMillis(), TimeUnit.MILLISECONDS)) {
                appendLog("[ceobe] 优雅停止超时，强制结束进程");
                p.destroyForcibly();
                p.waitFor(10, TimeUnit.SECONDS);
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            p.destroyForcibly();
        }
    }

    public void kill() {
        Process p = process;
        if (p != null && p.isAlive()) {
            stopRequested = true;
            p.destroyForcibly();
        }
    }

    private synchronized void appendLog(String line) {
        synchronized (ring) {
            if (ring.size() >= RING_SIZE) ring.pollFirst();
            ring.addLast(line);
        }
        try {
            Files.writeString(dir.resolve("console.log"), line + System.lineSeparator(),
                    StandardCharsets.UTF_8, StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        } catch (IOException ignored) {
            // 控制台日志落盘失败不阻塞服务
        }
        publish(new InstanceEvent.LogLine(meta.name, line));
    }

    private void transition(InstanceState next, int exitCode) {
        if (state == next && next != InstanceState.STOPPED) return;
        state = next;
        writeRuntimeFiles();
        publish(new InstanceEvent.StateChanged(meta.name, next, exitCode));
    }

    private void writeRuntimeFiles() {
        try {
            Files.writeString(dir.resolve(".state"), state.name());
            Process p = process;
            if (p != null && p.isAlive()) {
                Files.writeString(dir.resolve(".pid"), String.valueOf(p.pid()));
            } else {
                Files.deleteIfExists(dir.resolve(".pid"));
            }
        } catch (IOException ignored) {
        }
    }

    private void publish(InstanceEvent event) {
        for (InstanceListener l : listeners) {
            try {
                l.onEvent(event);
            } catch (RuntimeException ignored) {
                // 单个监听器异常不影响其他
            }
        }
    }

    private static void checkPortFree(int port) {
        try (ServerSocket socket = new ServerSocket()) {
            socket.setReuseAddress(false);
            socket.bind(new InetSocketAddress("0.0.0.0", port), 1);
        } catch (IOException e) {
            throw CeobeException.of("端口 %d 已被占用，请改用其他端口", port);
        }
    }
}
