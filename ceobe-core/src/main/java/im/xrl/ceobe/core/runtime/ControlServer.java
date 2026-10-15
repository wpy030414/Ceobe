package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.util.CeobeException;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * 实例控制通道服务端：随监督进程启动，绑定 127.0.0.1 随机端口 + 随机 token。
 * <p>协议：客户端 {@code AUTH <token>} → {@code OK}；随后每行一条命令，
 * 应答以 {@code OK}/{@code ERR <msg>} 起头，正文行后跟 {@code END}。
 */
public final class ControlServer implements AutoCloseable {

    private final ServerProcess process;
    private final Path instanceDir;
    private final String instanceName;
    private final im.xrl.ceobe.core.tunnel.TunnelManager tunnels;
    private final ServerSocket server;
    private final String token = UUID.randomUUID().toString().replace("-", "");
    private final AtomicBoolean closed = new AtomicBoolean();

    public ControlServer(ServerProcess process, Path instanceDir, String instanceName,
                         im.xrl.ceobe.core.tunnel.TunnelManager tunnels) {
        this.process = process;
        this.instanceDir = instanceDir;
        this.instanceName = instanceName;
        this.tunnels = tunnels;
        try {
            this.server = new ServerSocket();
            this.server.bind(new InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 4);
        } catch (IOException e) {
            throw new CeobeException("无法开启实例控制通道: " + e.getMessage(), e);
        }
        InstanceControl.writeCtl(instanceDir, server.getLocalPort(), token);
        Thread.ofVirtual().name("ceobe-ctl-accept").start(this::acceptLoop);
    }

    private void acceptLoop() {
        while (!closed.get()) {
            try {
                Socket socket = server.accept();
                Thread.ofVirtual().start(() -> handle(socket));
            } catch (IOException e) {
                if (!closed.get()) {
                    // 单次 accept 失败不应终止服务
                }
            }
        }
    }

    private void handle(Socket socket) {
        try (socket;
             var in = new BufferedReader(new InputStreamReader(socket.getInputStream(),
                     StandardCharsets.UTF_8));
             var out = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(),
                     StandardCharsets.UTF_8), true)) {
            String auth = in.readLine();
            if (auth == null || !auth.equals("AUTH " + token)) {
                out.println("ERR 鉴权失败");
                out.println("END");
                return;
            }
            out.println("OK");
            String line;
            while ((line = in.readLine()) != null) {
                dispatch(line, out);
            }
        } catch (IOException ignored) {
        }
    }

    private void dispatch(String line, PrintWriter out) {
        try {
            if (line.startsWith("EXEC ")) {
                process.sendCommand(line.substring(5));
                out.println("OK");
            } else if (line.equals("STATE")) {
                out.println("OK");
                out.println(process.state().name());
            } else if (line.startsWith("LOGS ")) {
                int tail = Integer.parseInt(line.substring(5).trim());
                List<String> logs = process.recentLogs(tail);
                out.println("OK");
                logs.forEach(out::println);
            } else if (line.equals("STOP")) {
                out.println("OK");
                out.println("END");
                out.flush();
                Thread.ofVirtual().start(() -> process.stop(java.time.Duration.ofSeconds(90)));
                return;
            } else if (line.startsWith("TUNNEL ")) {
                dispatchTunnel(line.substring(7).trim(), out);
            } else if (line.equals("KILL")) {
                process.kill();
                out.println("OK");
            } else {
                out.println("ERR 未知命令: " + line);
            }
        } catch (RuntimeException e) {
            out.println("ERR " + e.getMessage());
        }
        out.println("END");
    }

    /** 隧道子命令：UP <kind> / DOWN <kind> / STATUS（由 dispatch 统一补 END）。 */
    private void dispatchTunnel(String args, PrintWriter out) {
        try {
            if (tunnels == null) {
                out.println("ERR 该实例未启用隧道管理");
                return;
            }
            if (args.equals("STATUS")) {
                out.println("OK");
                for (var s : tunnels.activeOf(instanceName)) {
                    out.println(s.kind.id() + "|" + s.publicUrl + "|"
                            + (s.expiresAt == null ? "-" : s.expiresAt));
                }
            } else if (args.startsWith("UP ")) {
                var kind = im.xrl.ceobe.core.tunnel.TunnelKind.fromId(args.substring(3).trim());
                var session = tunnels.up(instanceName, kind);
                out.println("OK");
                out.println(session.publicUrl == null ? "-" : session.publicUrl);
                out.println(session.expiresAt == null ? "-" : session.expiresAt.toString());
            } else if (args.startsWith("DOWN ")) {
                var kind = im.xrl.ceobe.core.tunnel.TunnelKind.fromId(args.substring(5).trim());
                tunnels.down(instanceName, kind);
                out.println("OK");
            } else {
                out.println("ERR 未知隧道命令: " + args);
            }
        } catch (RuntimeException e) {
            out.println("ERR " + e.getMessage());
        }
    }

    @Override
    public void close() {
        closed.set(true);
        InstanceControl.deleteCtl(instanceDir);
        try {
            server.close();
        } catch (IOException ignored) {
        }
    }
}
