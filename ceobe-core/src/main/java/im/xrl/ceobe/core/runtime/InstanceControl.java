package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.util.CeobeException;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.net.InetAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/**
 * 实例控制通道客户端（.ctl 文件 = {@code port:token}）。
 * <p>由托管实例的「监督进程」开启，供其他 CLI 进程实现 {@code stop / exec / state / logs}，
 * 从而无需常驻全局 daemon（见 ADR-005）。
 */
public final class InstanceControl {

    private static final String CTL_FILE = ".ctl";

    private InstanceControl() {
    }

    /** 控制通道是否可用（存在 .ctl 且端口可连）。 */
    public static boolean isAvailable(Path instanceDir) {
        return readCtl(instanceDir).isPresent();
    }

    private static Optional<String[]> readCtl(Path instanceDir) {
        Path ctl = instanceDir.resolve(CTL_FILE);
        if (!Files.isRegularFile(ctl)) return Optional.empty();
        try {
            String content = Files.readString(ctl, StandardCharsets.UTF_8).trim();
            String[] parts = content.split(":", 2);
            if (parts.length != 2) return Optional.empty();
            return Optional.of(parts);
        } catch (IOException e) {
            return Optional.empty();
        }
    }

    static void writeCtl(Path instanceDir, int port, String token) {
        try {
            Files.writeString(instanceDir.resolve(CTL_FILE), port + ":" + token, StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new CeobeException("写入控制通道信息失败", e);
        }
    }

    static void deleteCtl(Path instanceDir) {
        try {
            Files.deleteIfExists(instanceDir.resolve(CTL_FILE));
        } catch (IOException ignored) {
        }
    }

    /** 默认往返超时（秒）；隧道建立等长操作使用更长的 {@link #LONG_TIMEOUT_SECONDS}。 */
    public static final int DEFAULT_TIMEOUT_SECONDS = 15;
    public static final int LONG_TIMEOUT_SECONDS = 150;

    /** 单条命令往返（默认超时）。 */
    public static List<String> call(Path instanceDir, String command) {
        return call(instanceDir, command, DEFAULT_TIMEOUT_SECONDS);
    }

    /** 单条命令往返。连接被拒时清理陈旧 .ctl 并给出可操作提示。 */
    public static List<String> call(Path instanceDir, String command, int timeoutSeconds) {
        String[] ctl = readCtl(instanceDir).orElseThrow(
                () -> new CeobeException("实例未由监督进程托管（无控制通道），无法执行该操作"));
        int port = Integer.parseInt(ctl[0]);
        String token = ctl[1];
        try (Socket socket = new Socket(InetAddress.getLoopbackAddress(), port)) {
            socket.setSoTimeout(timeoutSeconds * 1000);
            var out = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(),
                    StandardCharsets.UTF_8), true);
            var in = new BufferedReader(new InputStreamReader(socket.getInputStream(),
                    StandardCharsets.UTF_8));
            out.println("AUTH " + token);
            String auth = in.readLine();
            if (auth == null || !auth.equals("OK")) {
                throw new CeobeException("控制通道鉴权失败");
            }
            out.println(command);
            List<String> lines = new ArrayList<>();
            String line;
            while ((line = in.readLine()) != null && !line.equals("END")) {
                lines.add(line);
            }
            return parse(lines);
        } catch (java.net.ConnectException e) {
            clear(instanceDir);
            throw new CeobeException("控制通道已失效（监督进程可能已退出），请重新 `ceobe start -d`");
        } catch (java.net.SocketTimeoutException e) {
            throw CeobeException.of("控制通道响应超时（%d 秒）：该操作可能仍在后台继续", timeoutSeconds);
        } catch (IOException e) {
            throw new CeobeException("控制通道连接失败（实例可能已停止）: " + e.getMessage(), e);
        }
    }

    /** 清理控制通道信息（启动新监督进程前调用，避免陈旧端口误判）。 */
    public static void clear(Path instanceDir) {
        deleteCtl(instanceDir);
    }

    /**
     * 解析控制通道应答：剥离 {@code OK} / {@code OK …} 前缀行，遇 {@code ERR …} 转异常。
     * <p>回归点：服务端应答前缀无尾随空格，早期实现只剥离 {@code "OK "} 导致状态永远读不到。
     */
    static List<String> parse(List<String> lines) {
        if (!lines.isEmpty() && lines.get(0).startsWith("ERR ")) {
            throw new CeobeException(lines.get(0).substring(4));
        }
        if (!lines.isEmpty() && (lines.get(0).equals("OK") || lines.get(0).startsWith("OK "))) {
            lines.remove(0);
        }
        return lines;
    }

    public static void stop(Path instanceDir) {
        call(instanceDir, "STOP");
    }

    public static void kill(Path instanceDir) {
        call(instanceDir, "KILL");
    }

    public static void exec(Path instanceDir, String command) {
        call(instanceDir, "EXEC " + command);
    }

    public static Optional<InstanceState> state(Path instanceDir) {
        if (!isAvailable(instanceDir)) return Optional.empty();
        List<String> lines = call(instanceDir, "STATE");
        if (lines.isEmpty()) return Optional.empty();
        try {
            return Optional.of(InstanceState.valueOf(lines.get(0).trim()));
        } catch (IllegalArgumentException e) {
            return Optional.empty();
        }
    }

    public static List<String> logs(Path instanceDir, int tail) {
        return call(instanceDir, "LOGS " + tail);
    }

    /** 开启隧道，返回 [公网地址, 有效期]（第二项可能为 "-"）；建链较慢，使用长超时。 */
    public static List<String> tunnelUp(Path instanceDir, String kindId) {
        return call(instanceDir, "TUNNEL UP " + kindId, LONG_TIMEOUT_SECONDS);
    }

    public static void tunnelDown(Path instanceDir, String kindId) {
        call(instanceDir, "TUNNEL DOWN " + kindId);
    }

    /** 活动通道列表，每项形如 {@code kind|url|expiresAt}。 */
    public static List<String> tunnelStatus(Path instanceDir) {
        return call(instanceDir, "TUNNEL STATUS");
    }
}
