package im.xrl.ceobe.core.webconsole;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.Json;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 内嵌只读 Web 控制台（JDK HttpServer）。
 * <p>仅绑定 127.0.0.1，公网可达性完全由 Cloudflare 隧道承担（随机地址即隐式凭证）。
 */
public final class WebConsole implements AutoCloseable {

    private final HttpServer server;
    private final InstanceManager instances;

    public WebConsole(InstanceManager instances, int port) {
        this.instances = instances;
        try {
            server = HttpServer.create(new InetSocketAddress("127.0.0.1", port), 0);
        } catch (IOException e) {
            throw new IllegalStateException("Web 控制台启动失败（端口 " + port + "）: " + e.getMessage(), e);
        }
        server.createContext("/", this::handleIndex);
        server.createContext("/api/instances", this::handleInstances);
        server.setExecutor(java.util.concurrent.Executors.newVirtualThreadPerTaskExecutor());
        server.start();
    }

    public int port() {
        return server.getAddress().getPort();
    }

    private void handleIndex(HttpExchange ex) throws IOException {
        respondHtml(ex, INDEX_PAGE);
    }

    private void handleInstances(HttpExchange ex) throws IOException {
        String path = ex.getRequestURI().getPath();
        if (path.startsWith("/api/instances/") && path.endsWith("/logs")) {
            String name = path.substring("/api/instances/".length(), path.length() - "/logs".length());
            handleLogs(ex, name);
            return;
        }
        List<Map<String, Object>> payload = instances.list().stream().map(this::toJson).toList();
        respondJson(ex, Json.write(Map.of("instances", payload)));
    }

    private void handleLogs(HttpExchange ex, String name) throws IOException {
        int tail = queryInt(ex, "tail", 200);
        try {
            InstanceStatus status = instances.require(name);
            List<String> logs = instances.processOf(name)
                    .map(p -> p.recentLogs(tail))
                    .orElseGet(() -> readFileTail(instances.dir(name).resolve("console.log"), tail));
            Map<String, Object> payload = new LinkedHashMap<>();
            payload.put("instance", status.meta().name);
            payload.put("state", status.state().name());
            payload.put("lines", logs);
            respondJson(ex, Json.write(payload));
        } catch (RuntimeException e) {
            respondJson(ex, Json.write(Map.of("error", e.getMessage())), 404);
        }
    }

    private Map<String, Object> toJson(InstanceStatus s) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("name", s.meta().name);
        m.put("type", s.meta().serverType.name());
        m.put("version", s.meta().gameVersion);
        m.put("port", s.meta().port);
        m.put("state", s.state().name());
        m.put("stateLabel", s.state().zhLabel());
        m.put("address", s.pinggyAddress());
        return m;
    }

    private static List<String> readFileTail(java.nio.file.Path file, int tail) {
        try {
            if (!java.nio.file.Files.isRegularFile(file)) return List.of();
            List<String> all = java.nio.file.Files.readAllLines(file, StandardCharsets.UTF_8);
            int from = Math.max(0, all.size() - tail);
            return all.subList(from, all.size());
        } catch (IOException e) {
            return List.of();
        }
    }

    private static int queryInt(HttpExchange ex, String key, int def) {
        String q = ex.getRequestURI().getQuery();
        if (q == null) return def;
        for (String pair : q.split("&")) {
            String[] kv = pair.split("=", 2);
            if (kv.length == 2 && kv[0].equals(key)) {
                try {
                    return Integer.parseInt(kv[1]);
                } catch (NumberFormatException ignored) {
                }
            }
        }
        return def;
    }

    private static void respondJson(HttpExchange ex, String body) throws IOException {
        respondJson(ex, body, 200);
    }

    private static void respondJson(HttpExchange ex, String body, int code) throws IOException {
        respond(ex, body, code, "application/json; charset=utf-8");
    }

    private static void respondHtml(HttpExchange ex, String body) throws IOException {
        respond(ex, body, 200, "text/html; charset=utf-8");
    }

    private static void respond(HttpExchange ex, String body, int code, String contentType)
            throws IOException {
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().set("Content-Type", contentType);
        ex.sendResponseHeaders(code, bytes.length);
        try (OutputStream out = ex.getResponseBody()) {
            out.write(bytes);
        }
    }

    @Override
    public void close() {
        server.stop(0);
    }

    private static final String INDEX_PAGE = """
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>ceobe 控制台</title>
            <style>
              :root{--bg:#09090b;--card:#18181b;--fg:#fafafa;--muted:#a1a1aa;--border:#27272a;--ring:#3f3f46}
              *{box-sizing:border-box}
              body{margin:0;padding:32px;background:var(--bg);color:var(--fg);
                   font:14px/1.6 Inter,system-ui,-apple-system,"Segoe UI",sans-serif}
              h1{font-size:20px;font-weight:600;margin:0 0 4px}
              .sub{color:var(--muted);margin-bottom:24px}
              .grid{display:grid;gap:16px;grid-template-columns:repeat(auto-fill,minmax(300px,1fr))}
              .card{background:var(--card);border:1px solid var(--border);border-radius:8px;padding:16px}
              .name{font-weight:600;margin-bottom:8px}
              .row{display:flex;justify-content:space-between;color:var(--muted);font-size:13px}
              .addr{margin-top:10px;padding:8px;border:1px dashed var(--ring);border-radius:6px;
                    font-family:ui-monospace,Consolas,monospace;font-size:12px;word-break:break-all}
              .badge{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px;
                     background:#27272a;color:#a1a1aa}
              .RUNNING{background:#052e16;color:#4ade80}
              .CRASHED{background:#450a0a;color:#f87171}
              .STARTING,.STOPPING{background:#422006;color:#fbbf24}
              pre{background:#09090b;border:1px solid var(--border);border-radius:8px;padding:12px;
                  overflow:auto;max-height:320px;font-size:12px}
            </style></head><body>
            <h1>ceobe 控制台</h1>
            <div class="sub">只读视图 · 数据来自本机 ceobe 核心</div>
            <div class="grid" id="grid"></div>
            <script>
            async function tick(){
              const r = await fetch('/api/instances'); const d = await r.json();
              const g = document.getElementById('grid'); g.innerHTML='';
              for(const i of d.instances){
                const c=document.createElement('div'); c.className='card';
                c.innerHTML = `<div class="name">${i.name} <span class="badge ${i.state}">${i.stateLabel}</span></div>
                  <div class="row"><span>${i.type} ${i.version}</span><span>端口 ${i.port}</span></div>
                  <div class="addr">${i.address ?? ('127.0.0.1:'+i.port)}</div>`;
                g.appendChild(c);
              }
            }
            tick(); setInterval(tick, 5000);
            </script></body></html>
            """;
}
