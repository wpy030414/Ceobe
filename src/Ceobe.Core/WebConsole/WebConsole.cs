using System.Net;
using System.Net.Sockets;
using System.Text;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Core.WebConsole;

/// <summary>
/// 内嵌只读 Web 控制台。
///
/// 为什么不用 <see cref="HttpListener"/>：Java 版用的是 JDK <c>com.sun.net.httpserver</c>，
/// 那是裸 socket；而 Windows 的 HttpListener 走 http.sys，非管理员绑定任意端口会被 urlacl
/// 判为 Access Denied，要求事先 <c>netsh http add urlacl</c>。本产品的目标用户是「双击开服的普通玩家」，
/// 不能假设他有管理员权限，故这里用 TcpListener 自研极简 HTTP/1.1 响应，与 Java 版能力对齐。
///
/// 安全边界：仅绑定 127.0.0.1；公网可达性完全交给 Cloudflare 隧道，隧道的随机子域即隐式凭证。
/// 因此本页**只读**——暴露状态与日志，不接受任何控制命令。若要加「远程停服」这类写操作，
/// 必须先引入鉴权，不能沿用当前模型。
/// </summary>
public sealed class WebConsole : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly InstanceManager _instances;
    private readonly Task _loop;
    private volatile bool _closed;

    /// <param name="instances">数据源</param>
    /// <param name="port">0 = 自动选取空闲端口（GUI 用；CLI <c>ceobe web</c> 可显式指定）</param>
    public WebConsole(InstanceManager instances, int port = 0)
    {
        _instances = instances;
        var actual = port == 0 ? PortProbe.FindFree(8737) : port;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, actual);
            _listener.Start();
        }
        catch (Exception e) when (e is SocketException or UnauthorizedAccessException)
        {
            throw new CeobeException($"Web 控制台启动失败（端口 {actual}）: {e.Message}", e);
        }
        Port = actual;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private async Task AcceptLoop()
    {
        while (!_closed)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // Stop() 后正常退出
            }
            _ = Task.Run(() => HandleSafe(client));
        }
    }

    private async Task HandleSafe(TcpClient client)
    {
        using (client)
        {
            try
            {
                await Handle(client).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 只读页：任何异常都丢弃连接，不回显栈信息
            }
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var stream = client.GetStream();
        var (method, path, query) = await ReadRequestLine(stream).ConfigureAwait(false);
        if (method is null) return;

        // 只接受 GET：本服务无写语义，POST 一律 405，避免被当作可操作面板误用
        if (method != "GET")
        {
            await WriteAsync(stream, 405, "application/json; charset=utf-8",
                JsonUtil.Write(new { error = "method not allowed" })).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/api/instances/", StringComparison.Ordinal)
            && path.EndsWith("/logs", StringComparison.Ordinal))
        {
            var name = path["/api/instances/".Length..^"/logs".Length];
            await HandleLogs(stream, name, query).ConfigureAwait(false);
            return;
        }
        if (path == "/api/instances")
        {
            var payload = _instances.List().Select(ToJson).ToList();
            await WriteAsync(stream, 200, "application/json; charset=utf-8",
                JsonUtil.Write(new { instances = payload })).ConfigureAwait(false);
            return;
        }
        if (path is "/" or "/index.html")
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", IndexPage).ConfigureAwait(false);
            return;
        }
        await WriteAsync(stream, 404, "application/json; charset=utf-8",
            JsonUtil.Write(new { error = "not found" })).ConfigureAwait(false);
    }

    private async Task HandleLogs(NetworkStream stream, string name, string query)
    {
        var tail = QueryInt(query, "tail", 200);
        var status = _instances.Find(name);
        if (status is null)
        {
            await WriteAsync(stream, 404, "application/json; charset=utf-8",
                JsonUtil.Write(new { error = $"实例不存在: {name}" })).ConfigureAwait(false);
            return;
        }
        // 本进程托管走环缓冲，否则读 console.log 尾部（对齐 Java 版 orElseGet）
        var logs = _instances.ProcessOf(name)?.RecentLogs(tail) ?? ReadFileTail(name, tail);
        await WriteAsync(stream, 200, "application/json; charset=utf-8",
            JsonUtil.Write(new
            {
                instance = status.Meta.Name,
                state = status.State.ToString(),
                lines = logs,
            })).ConfigureAwait(false);
    }

    private static object ToJson(InstanceStatus s) => new
    {
        name = s.Meta.Name,
        type = s.Meta.ServerType.ToString(),
        version = s.Meta.GameVersion,
        port = s.Meta.Port,
        state = s.State.ToString(),
        stateLabel = s.State.ZhLabel(),
        address = s.Meta.LastPinggyAddress,
    };

    private List<string> ReadFileTail(string name, int tail)
    {
        var file = Path.Combine(_instances.Dir(name), "console.log");
        if (!File.Exists(file)) return [];
        try
        {
            var all = File.ReadAllLines(file, new UTF8Encoding(false));
            return all.Length <= tail ? all.ToList() : all[^tail..].ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>极简请求行解析：只取 method / path / query，请求头不消费（客户端断开即可）。</summary>
    private static async Task<(string? Method, string Path, string Query)> ReadRequestLine(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder(4096);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sb.Length < 4096 && DateTime.UtcNow < deadline)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            if (sb.ToString().Contains("\r\n", StringComparison.Ordinal)) break;
        }
        var requestLine = sb.ToString().Split("\r\n")[0];
        if (requestLine.Length == 0) return (null, "/", "");

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return (null, "/", "");
        var target = parts[1];
        var q = target.IndexOf('?');
        return q < 0
            ? (parts[0], UrlDecode(target), "")
            : (parts[0], UrlDecode(target[..q]), target[(q + 1)..]);
    }

    /// <summary>实例名受 [a-z0-9_-] 约束，path 解码只是防御性处理，失败即用原文。</summary>
    private static string UrlDecode(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (Exception)
        {
            return s;
        }
    }

    private static int QueryInt(string query, string key, int def)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out var v)) return v;
        }
        return def;
    }

    private static async Task WriteAsync(NetworkStream stream, int code, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(code).Append(' ').Append(Reason(code)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
            // 只读页 + 走公网隧道：禁缓存避免旧地址残留页面，禁 iframe 降低被借用的面
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "OK",
    };

    public ValueTask DisposeAsync()
    {
        _closed = true;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>只读首页：与 Java 版同一份 zinc 暗色样式，5 秒轮询。</summary>
    private const string IndexPage = """
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
