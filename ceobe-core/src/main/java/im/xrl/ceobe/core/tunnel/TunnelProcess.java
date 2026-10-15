package im.xrl.ceobe.core.tunnel;

import im.xrl.ceobe.core.util.CeobeException;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;
import java.util.function.Predicate;
import java.util.function.Supplier;

/** 隧道子进程通用工具：启动、逐行日志泵、就绪等待、终止。 */
final class TunnelProcess {

    /** 启动子进程，泵日志到 logSink + 落盘 tunnel.log，直到 ready 提供非空值或超时。 */
    static void run(ProcessBuilder pb, Path instanceDir, Consumer<String> logSink,
                    Consumer<String> lineParser, Supplier<String> ready,
                    long timeoutMs, TunnelSession session) {
        pb.redirectErrorStream(true);
        Process p;
        try {
            p = pb.start();
        } catch (IOException e) {
            throw new CeobeException("无法启动隧道进程: " + e.getMessage(), e);
        }
        session.process = p;
        Path logFile = instanceDir.resolve("tunnel.log");
        Thread pump = Thread.ofVirtual().name("ceobe-tunnel").start(() -> {
            try (var reader = new java.io.BufferedReader(
                    new java.io.InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = reader.readLine()) != null) {
                    logSink.accept("[tunnel] " + line);
                    appendTo(logFile, line);
                    lineParser.accept(line);
                }
            } catch (IOException ignored) {
            }
        });

        long deadline = System.currentTimeMillis() + timeoutMs;
        while (System.currentTimeMillis() < deadline) {
            if (ready.get() != null) return;
            if (!p.isAlive()) {
                throw new CeobeException("隧道进程提前退出（退出码 " + p.exitValue()
                        + "），请查看 tunnel.log");
            }
            try {
                Thread.sleep(250);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new CeobeException("等待隧道就绪被中断", e);
            }
        }
        p.destroyForcibly();
        throw new CeobeException("隧道在 %d 秒内未就绪，请查看 tunnel.log".formatted(timeoutMs / 1000));
    }

    static void terminate(TunnelSession session) {
        Process p = session.process;
        if (p == null) return;
        p.destroy();
        try {
            if (!p.waitFor(5, TimeUnit.SECONDS)) p.destroyForcibly();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            p.destroyForcibly();
        }
        session.process = null;
    }

    /** 在 PATH 中查找可执行文件（Windows 兼容 .exe/.cmd）。 */
    static String which(String name) {
        String path = System.getenv("PATH");
        if (path == null) return null;
        boolean win = System.getProperty("os.name", "").toLowerCase().contains("win");
        List<String> exts = win ? List.of(".exe", ".cmd", ".bat", "") : List.of("");
        for (String dir : path.split(java.io.File.pathSeparator)) {
            for (String ext : exts) {
                Path candidate = Path.of(dir, name + ext);
                if (Files.isRegularFile(candidate) && Files.isExecutable(candidate)) {
                    return candidate.toString();
                }
            }
        }
        return null;
    }

    private static void appendTo(Path file, String line) {
        try {
            Files.writeString(file, line + System.lineSeparator(), StandardCharsets.UTF_8,
                    StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        } catch (IOException ignored) {
        }
    }

    private TunnelProcess() {
    }
}
