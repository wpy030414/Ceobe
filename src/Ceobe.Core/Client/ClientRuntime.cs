using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// 客户端进程的持有者。与服务端的 <see cref="Runtime.InstanceManager"/> 平行，但刻意更薄：
///
/// · 客户端**没有监督进程**——游戏窗口就是用户直接交互的对象，杀掉它等于杀掉游戏，
///   不像服务端需要跨进程控制通道；
/// · 状态只在内存里（进程存活即运行中），不写 <c>.state</c>/<c>.pid</c>；
/// · 退出后唯一要落盘的是「上次启动时间 / 时长 / 是否崩溃」，供启动页展示。
///
/// 唯一必须做扎实的是日志：游戏的所有失败信息都只在 stdout 里，
/// 且 1.20 之后 nohup 型的输出会被 log4j 缓冲——所以要用 events 逐行读，不能靠事后读文件。
/// </summary>
public sealed class ClientRuntime
{
    /// <summary>每个实例保留的内存日志行数（界面滚动用；磁盘上的 latest.log 才是全文）。</summary>
    private const int RingSize = 800;

    private readonly object _gate = new();
    private readonly Dictionary<string, RunningClient> _running = new(StringComparer.Ordinal);

    /// <summary>日志与状态事件。订阅方须自行 marshal 到 UI 线程。</summary>
    public event Action<IClientEvent>? OnEvent;

    public bool IsRunning(string name)
    {
        lock (_gate)
        {
            return _running.TryGetValue(name, out var c) && c.IsAlive;
        }
    }

    public ClientState StateOf(string name)
    {
        lock (_gate)
        {
            return _running.TryGetValue(name, out var c) ? c.State : ClientState.STOPPED;
        }
    }

    public int? PidOf(string name)
    {
        lock (_gate)
        {
            return _running.TryGetValue(name, out var c) && c.IsAlive ? c.Pid : null;
        }
    }


    /// <summary>
    /// 认领上一个 GUI 会话遗留的客户端进程：按 client.pid 里的 pid + 启动时间校验身份，
    /// 通过就恢复为「运行中」，用户仍能停止它（关窗口或强杀）；校验失败则清掉 pid 文件。
    /// 客户端没有控制通道，pid + 启动时间是唯一能防止 pid 复用的身份判据。
    /// </summary>
    public int AdoptAll()
    {
        var adopted = 0;
        foreach (var meta in ClientStore.List())
        {
            var pidPath = ClientStore.PidFile(meta.Name);
            if (!File.Exists(pidPath)) continue;
            try
            {
                var parts = File.ReadAllText(pidPath).Split('|');
                if (parts.Length < 2
                    || !int.TryParse(parts[0], out var pid)
                    || !long.TryParse(parts[1], out var ticks))
                {
                    File.Delete(pidPath);
                    continue;
                }
                var process = Process.GetProcessById(pid);
                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                if (process.HasExited || Math.Abs(startTicks - ticks) > TimeSpan.FromSeconds(2).Ticks)
                {
                    process.Dispose();
                    File.Delete(pidPath);
                    continue;
                }
                var client = new RunningClient(meta, process.StartTime.ToUniversalTime());
                client.AttachAdopted(process);
                client.State = ClientState.RUNNING;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => OnExited(meta.Name, client);
                lock (_gate) _running[meta.Name] = client;
                Publish(new ClientStateChanged(meta.Name, ClientState.RUNNING, 0));
                adopted++;
            }
            catch (Exception)
            {
                try { File.Delete(pidPath); } catch (Exception) { }
            }
        }
        return adopted;
    }

    private static void WritePid(string name, Process process)
    {
        try
        {
            File.WriteAllText(ClientStore.PidFile(name),
                process.Id + "|" + process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception)
        {
            // 落盘失败只影响「重启后认领」，不影响本次运行
        }
    }

    private static void TryDeletePid(string name)
    {
        try { File.Delete(ClientStore.PidFile(name)); } catch (Exception) { }
    }

    public IReadOnlyList<string> RecentLogs(string name, int max)
    {
        lock (_gate)
        {
            return _running.TryGetValue(name, out var c) ? c.Recent(max) : [];
        }
    }

    /// <summary>
    /// 启动。同步只做「拼命令 + 起进程」（几百毫秒）；下载/修复请先走 <see cref="ClientInstaller"/>。
    /// 失败会抛 <see cref="CeobeException"/>，消息里必须带可执行的下一步。
    /// </summary>
    public LaunchPlan Start(ClientMeta meta, ClientAccount account, string? serverAddress = null)
    {
        if (IsRunning(meta.Name))
            throw CeobeException.Of("客户端 {0} 已在运行", meta.Name);

        // 启动前把 Ceobe 服务端地址写进 servers.dat：多人列表里直接可见，不用手输 IP
        TrySyncServers(meta);

        var plan = ClientLauncher.Build(meta, account, serverAddress);
        var psi = new ProcessStartInfo
        {
            FileName = plan.JavaExe,
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);

        var client = new RunningClient(meta, plan);
        lock (_gate)
        {
            _running[meta.Name] = client;
        }

        Publish(new ClientStateChanged(meta.Name, ClientState.STARTING, 0));
        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => client.OnLine(e.Data, Publish);
            process.ErrorDataReceived += (_, e) => client.OnLine(e.Data, Publish);
            process.Exited += (_, _) => OnExited(meta.Name, client);
            process.Start();
            client.Attach(process);
            WritePid(meta.Name, process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _running.Remove(meta.Name);
            }
            client.Dispose();
            Publish(new ClientStateChanged(meta.Name, ClientState.STOPPED, -1));
            throw new CeobeException(
                $"无法启动 Java 进程：{e.Message}\n下一步：确认「设置」里的 JDK 目录可用，或换一个 Java 版本。", e);
        }

        client.State = ClientState.RUNNING;
        Publish(new ClientStateChanged(meta.Name, ClientState.RUNNING, 0));
        Publish(new ClientLogLine(meta.Name, "[Ceobe] " + plan.DisplayCommand));
        return plan;
    }

    /// <summary>
    /// 停止。先请求关闭窗口（游戏会自己存盘退出），超时才强杀——
    /// 直接 Kill 会让最后一段存档丢失，这是启动器最不该犯的错。
    /// </summary>
    public async Task<bool> StopAsync(string name, bool force = false, TimeSpan? grace = null,
        CancellationToken ct = default)
    {
        RunningClient? client;
        lock (_gate)
        {
            if (!_running.TryGetValue(name, out client) || !client.IsAlive) return false;
        }

        client.State = ClientState.STOPPING;
        Publish(new ClientStateChanged(name, ClientState.STOPPING, 0));

        if (!force)
        {
            try { client.CloseMainWindow(); }
            catch (Exception) { /* 没有主窗口（启动早期崩溃）就直接 escalation */ }
        }

        var timeout = grace ?? TimeSpan.FromSeconds(15);
        try
        {
            if (!client.WaitForExit(timeout, ct) && client.IsAlive)
            {
                client.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            client.Kill();
        }
        return true;
    }

    /// <summary>强制终止（游戏卡死时用；确认对话框在 View 层）。</summary>
    public void Kill(string name)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(name, out var c)) c.Kill();
        }
    }

    private void OnExited(string name, RunningClient client)
    {
        client.Dispose();
        TryDeletePid(name);
        lock (_gate)
        {
            if (_running.TryGetValue(name, out var current) && ReferenceEquals(current, client))
            {
                _running.Remove(name);
            }
        }

        var exitCode = client.ExitCode;
        var state = exitCode == 0 ? ClientState.STOPPED : ClientState.CRASHED;
        RecordSession(client.Meta, client.Duration, exitCode);
        Publish(new ClientStateChanged(name, state, exitCode));
        Publish(new ClientLogLine(name,
            exitCode == 0
                ? "[Ceobe] 游戏已正常退出"
                : $"[Ceobe] 游戏异常退出（退出码 {exitCode}）。到「日志」分区查看崩溃归因。"));
    }

    private static void RecordSession(ClientMeta meta, TimeSpan duration, int exitCode)
    {
        try
        {
            var latest = ClientStore.Find(meta.Name);
            if (latest is null) return;   // 实例刚被删
            latest.LastLaunchedAt = meta.LastLaunchedAt;
            latest.LastSessionSeconds = (int)Math.Max(0, duration.TotalSeconds);
            latest.LastExit = exitCode == 0 ? "normal" : "crashed";
            ClientStore.Save(latest);
        }
        catch (Exception)
        {
            // 记录会话失败不影响游戏本身
        }
    }

    /// <summary>
    /// 启动前把 Ceobe 服务端写进 servers.dat。失败不阻断启动——
    /// 多人列表少几条记录比「游戏起不来」轻得多，故只吞异常。
    /// </summary>
    private static void TrySyncServers(ClientMeta meta)
    {
        try
        {
            var entries = ClientServerLinks.Collect(meta);
            if (entries.Count > 0)
            {
                ServersDatFile.Sync(ClientStore.ServersDat(meta.Name), entries);
            }
        }
        catch (Exception)
        {
        }
    }

    private void Publish(IClientEvent evt)
    {
        foreach (var handler in OnEvent?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<IClientEvent>)handler)(evt);
            }
            catch (Exception)
            {
                // 单个订阅者异常不影响其它订阅者（对齐 InstanceManager.Publish）
            }
        }
    }

    /// <summary>一次运行的进程 + 日志缓冲。</summary>
    private sealed class RunningClient : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<string> _ring = new(RingSize);
        private readonly DateTimeOffset _started;
        private readonly string _logPath;
        private StreamWriter? _log;
        private Process? _process;

        public RunningClient(ClientMeta meta, LaunchPlan plan)
            : this(meta, DateTimeOffset.UtcNow)
        {
            Plan = plan;
        }

        /// <summary>认领模式：没有启动计划，启动时间取真实进程的 StartTime（会话时长才准确）。</summary>
        public RunningClient(ClientMeta meta, DateTimeOffset started)
        {
            Meta = meta;
            _started = started;
            _logPath = ClientStore.LatestLog(meta.Name);
        }

        public ClientMeta Meta { get; }
        public LaunchPlan? Plan { get; }
        public ClientState State { get; set; } = ClientState.STARTING;
        public TimeSpan Duration => DateTimeOffset.UtcNow - _started;

        public bool IsAlive
        {
            get
            {
                try { return _process is { HasExited: false }; }
                catch (Exception) { return false; }
            }
        }

        public int Pid
        {
            get
            {
                try { return _process?.Id ?? -1; }
                catch (Exception) { return -1; }
            }
        }

        public int ExitCode
        {
            get
            {
                try { return _process?.ExitCode ?? -1; }
                catch (Exception) { return -1; }
            }
        }

        public void Attach(Process process)
        {
            _process = process;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                _log = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = false };
                _log.WriteLine($"[Ceobe] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} 启动 {Meta.Name}（{Meta.DetailLine}）");
            }
            catch (Exception)
            {
                _log = null;   // 日志文件不可写不影响游戏
            }
        }

        /// <summary>认领已有进程：不截断 latest.log（那是上一轮会话的日志）。</summary>
        public void AttachAdopted(Process process) => _process = process;

        public void OnLine(string? line, Action<IClientEvent> publish)
        {
            if (line is null) return;
            lock (_lock)
            {
                if (_ring.Count >= RingSize) _ring.RemoveRange(0, RingSize / 8);
                _ring.Add(line);
                try
                {
                    _log?.WriteLine(line);
                    // 每行都 flush：游戏崩溃时进程被杀，缓冲区里最后几行往往正是原因
                    _log?.Flush();
                }
                catch (Exception)
                {
                }
            }
            publish(new ClientLogLine(Meta.Name, line));
        }

        public IReadOnlyList<string> Recent(int max)
        {
            lock (_lock)
            {
                return _ring.Count <= max ? _ring.ToArray() : _ring[^max..];
            }
        }

        public void CloseMainWindow()
        {
            try { _process?.CloseMainWindow(); }
            catch (Exception) { }
        }

        public bool WaitForExit(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                return _process?.WaitForExit((int)timeout.TotalMilliseconds) ?? true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Kill()
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _log?.Flush(); } catch (Exception) { }
                _log?.Dispose();
                _log = null;
            }
        }
    }
}
