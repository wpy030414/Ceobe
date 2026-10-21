using System.Net;
using System.Net.Sockets;
using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Core.Runtime;

/// <summary>
/// 隧道能力的最小视图，由 <c>Ceobe.Core.Tunnel.TunnelManager</c> 实现。
/// ControlServer 只认这个契约，避免 runtime ↔ tunnel 互相依赖（Java 版是硬引用具体类）。
/// </summary>
public interface IInstanceTunnels
{
    /// <summary>活动通道，每项为 (kindId, publicUrl, expiresAt；不过期为 null)。</summary>
    IReadOnlyList<(string Kind, string? Url, DateTimeOffset? ExpiresAt)> ActiveOf(string instanceName);

    /// <summary>开启通道，返回 (公网地址, 到期时间)。</summary>
    Task<(string? Url, DateTimeOffset? ExpiresAt)> UpAsync(string instanceName, string kindId, CancellationToken ct = default);

    void Down(string instanceName, string kindId);
}

/// <summary>
/// 实例控制通道服务端：随监督进程启动，绑定 127.0.0.1 随机端口 + 随机 token（ADR-007）。
/// 协议与 Java 版 <c>runtime/ControlServer</c> 一致，客户端见 <see cref="InstanceControl"/>。
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly ServerProcess _process;
    private readonly string _instanceDir;
    private readonly string _instanceName;
    private readonly IInstanceTunnels? _tunnels;
    private readonly TcpListener _listener;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private volatile bool _closed;

    public ControlServer(ServerProcess process, string instanceDir, string instanceName,
                         IInstanceTunnels? tunnels)
    {
        _process = process;
        _instanceDir = instanceDir;
        _instanceName = instanceName;
        _tunnels = tunnels;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        catch (Exception e) when (e is SocketException or UnauthorizedAccessException)
        {
            throw new CeobeException($"无法开启实例控制通道: {e.Message}", e);
        }
        InstanceControl.WriteCtl(instanceDir, ((IPEndPoint)_listener.LocalEndpoint).Port, _token);
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>本机控制端口（供 CLI 提示与测试）。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoop()
    {
        while (!_closed)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (SocketException)
            {
                if (!_closed) continue; // 单次 accept 失败不应终止服务
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var auth = reader.ReadLine();
                if (auth != "AUTH " + _token)
                {
                    writer.WriteLine("ERR 鉴权失败");
                    writer.WriteLine("END");
                    return;
                }
                writer.WriteLine("OK");

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    Dispatch(line, writer);
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private void Dispatch(string line, StreamWriter outWriter)
    {
        try
        {
            if (line.StartsWith("EXEC ", StringComparison.Ordinal))
            {
                _process.SendCommand(line[5..]);
                outWriter.WriteLine("OK");
            }
            else if (line == "STATE")
            {
                outWriter.WriteLine("OK");
                outWriter.WriteLine(_process.State.ToString());
            }
            else if (line.StartsWith("LOGS ", StringComparison.Ordinal))
            {
                var tail = int.Parse(line[5..].Trim());
                outWriter.WriteLine("OK");
                foreach (var l in _process.RecentLogs(tail)) outWriter.WriteLine(l);
            }
            else if (line == "STOP")
            {
                outWriter.WriteLine("OK");
                outWriter.WriteLine("END");
                outWriter.Flush();
                // 对齐 Java：先回应答，再在后台执行优雅停止
                _ = Task.Run(() => _process.Stop(TimeSpan.FromSeconds(90)));
                return;
            }
            else if (line.StartsWith("TUNNEL ", StringComparison.Ordinal))
            {
                DispatchTunnel(line[7..].Trim(), outWriter);
            }
            else if (line == "KILL")
            {
                _process.Kill();
                outWriter.WriteLine("OK");
            }
            else
            {
                outWriter.WriteLine("ERR 未知命令: " + line);
            }
        }
        catch (CeobeException e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
        catch (Exception e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
        outWriter.WriteLine("END");
    }

    /// <summary>隧道子命令：UP &lt;kind&gt; / DOWN &lt;kind&gt; / STATUS（END 由 dispatch 统一补）。</summary>
    private void DispatchTunnel(string args, StreamWriter outWriter)
    {
        if (_tunnels is null)
        {
            outWriter.WriteLine("ERR 该实例未启用隧道管理");
            return;
        }
        try
        {
            if (args == "STATUS")
            {
                outWriter.WriteLine("OK");
                foreach (var (kind, url, expires) in _tunnels.ActiveOf(_instanceName))
                {
                    outWriter.WriteLine($"{kind}|{url}|{(expires?.ToString("o") ?? "-")}");
                }
            }
            else if (args.StartsWith("UP ", StringComparison.Ordinal))
            {
                var kindId = args[3..].Trim();
                // 控制通道是同步逐行协议，此处阻塞等待建链完成（客户端超时 150 秒）
                var (url, expires) = _tunnels
                    .UpAsync(_instanceName, kindId)
                    .GetAwaiter().GetResult();
                outWriter.WriteLine("OK");
                outWriter.WriteLine(url ?? "-");
                outWriter.WriteLine(expires?.ToString("o") ?? "-");
            }
            else if (args.StartsWith("DOWN ", StringComparison.Ordinal))
            {
                _tunnels.Down(_instanceName, args[5..].Trim());
                outWriter.WriteLine("OK");
            }
            else
            {
                outWriter.WriteLine("ERR 未知隧道命令: " + args);
            }
        }
        catch (CeobeException e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
        catch (Exception e)
        {
            outWriter.WriteLine("ERR " + e.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        InstanceControl.DeleteCtl(_instanceDir);
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }
        return ValueTask.CompletedTask;
    }
}
