using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Runtime;

/// <summary>
/// 单个 MC 服务端进程的托管：启动、日志泵、状态机、控制台 stdin、优雅停止。
/// 语义照搬 Java 版 <c>runtime/ServerProcess</c>；状态推进依据：日志出现
/// <c>Done (…s)! For help</c> → RUNNING。
///
/// 与 Java 版的形态差异（不改语义）：
/// - Java 用虚拟线程跑 pump/watchdog，C# 用后台 Task；
/// - 事件从 listener 列表改为 <see cref="OnEvent"/> 事件，订阅者异常逐个吞掉（对齐原语义）。
/// </summary>
public sealed partial class ServerProcess
{
    /// <summary>「Done (2.231s)! For help」——vanilla 与各 fork 的就绪标志。</summary>
    private const string DonePattern = @"Done \([0-9.]+s\)! For help";

    private const int RingSize = 800;

    private readonly InstanceMeta _meta;
    private readonly string _dir;
    private readonly Queue<string> _ring = new();
    private readonly object _ringLock = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private volatile InstanceState _state = InstanceState.STOPPED;
    private volatile bool _stopRequested;

    public ServerProcess(InstanceMeta meta, string dir, IJavaLocator javaLocator)
    {
        _meta = meta;
        _dir = dir;
        JavaLocator = javaLocator;
    }

    public IJavaLocator JavaLocator { get; }

    /// <summary>事件订阅（GUI/CLI/Web 共用）。</summary>
    public event Action<IInstanceEvent>? OnEvent;

    public string InstanceName => _meta.Name;
    public InstanceMeta Meta => _meta;
    public InstanceState State => _state;
    public bool IsAlive => _process is { HasExited: false };

    public IReadOnlyList<string> RecentLogs(int n)
    {
        lock (_ringLock)
        {
            var all = _ring.ToArray();
            return all.Length <= n ? all : all[^n..];
        }
    }

    public void Start()
    {
        if (IsAlive) throw new CeobeException("实例已在运行");
        CheckPortFree(_meta.Port);

        string? authlib = null;
        if (!string.IsNullOrWhiteSpace(_meta.AuthlibInjectorUrl))
        {
            AppendLog("[Ceobe] 准备 authlib-injector（三方授权）…");
            // 对齐 Java 版：此处在启动路径上同步等待下载完成
            authlib = AuthlibInjector.EnsureAsync(NullProgress.Instance)
                .GetAwaiter().GetResult();
            AppendLog($"[Ceobe] 已启用 authlib-injector: {_meta.AuthlibInjectorUrl}");
        }

        var cmd = BuildCommand(authlib);
        // 落盘启动命令行：便于事后审计内存/GC/额外 JVM 参数
        AppendLog("$ " + string.Join(' ', cmd));

        var psi = new ProcessStartInfo
        {
            FileName = cmd[0],
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // redirectErrorStream(true) 的等价实现：stderr 并入同一泵
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // ArgumentList 是只读集合，只能逐个 Add（不能用初始化器赋值）
        foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(psi) ?? throw new CeobeException("启动失败: 进程未能创建");
        }
        catch (Exception e) when (e is not CeobeException)
        {
            throw new CeobeException($"启动失败: {e.Message}", e);
        }

        _stdin = _process.StandardInput;
        WriteRuntimeFiles();
        Transition(InstanceState.STARTING, 0);

        var p = _process;
        _ = Task.Run(() => Pump(p.StandardOutput));
        _ = Task.Run(() => Pump(p.StandardError));
        _ = Task.Run(() => Watchdog(p));
    }

    internal List<string> BuildCommand(string? authlib)
    {
        var cmd = new List<string>
        {
            JavaLauncher(),
            $"-Xms{Math.Max(256, _meta.MemoryMb / 2)}M",
            $"-Xmx{_meta.MemoryMb}M",
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=200",
            "-Dfile.encoding=UTF-8",
        };

        if (!string.IsNullOrWhiteSpace(_meta.JvmArgs))
        {
            cmd.AddRange(_meta.JvmArgs.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        if (authlib is not null)
        {
            cmd.Add($"-javaagent:{Path.GetFullPath(authlib)}={_meta.AuthlibInjectorUrl}");
        }
        if (!string.IsNullOrWhiteSpace(_meta.LaunchArgsFile))
        {
            // NeoForge 等：@参数文件启动（内含 -classpath / 主类 / --fml.* 参数）
            cmd.Add("@" + _meta.LaunchArgsFile);
        }
        else
        {
            cmd.Add("-jar");
            cmd.Add(_meta.LaunchJar);
        }
        cmd.Add("nogui");
        return cmd;
    }

    /// <summary>
    /// 定位用于拉起服务端的 java 启动器。
    /// ADR-018：不可盲目回退——「看起来是 JDK 但缺 bin/java.exe」的残破目录必须报错，
    /// 且提示可执行的下一步（本判定已下沉到 <see cref="IJavaLocator"/>）。
    /// </summary>
    private string JavaLauncher()
    {
        if (!string.IsNullOrWhiteSpace(_meta.JavaHome))
        {
            var launcher = Path.Combine(_meta.JavaHome, "bin", "java" + ExeSuffix());
            if (!File.Exists(launcher))
            {
                throw new CeobeException(
                    $"实例指定的 Java 运行时缺少启动器: {launcher}；请在实例设置中重新指定 JRE，" +
                    "或运行 ceobe jdk install 安装受管 JDK");
            }
            return launcher;
        }
        return JavaLocator.ResolveLauncher(null, _meta.GameVersion);
    }

    private static string ExeSuffix() => OperatingSystem.IsWindows() ? ".exe" : "";

    private void Pump(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                AppendLog(line);
                if (_state == InstanceState.STARTING && DoneRegex().IsMatch(line))
                {
                    Transition(InstanceState.RUNNING, 0);
                }
            }
        }
        catch (IOException)
        {
            // 进程结束后流关闭属正常
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Watchdog(Process p)
    {
        int code = -1;
        try
        {
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch (Exception)
        {
        }
        var finalState = _stopRequested || code == 0 ? InstanceState.STOPPED : InstanceState.CRASHED;
        _process = null;
        _stdin = null;
        Transition(finalState, code);
    }

    public void SendCommand(string command)
    {
        var w = _stdin;
        if (w is null) throw new CeobeException("实例未运行，无法发送命令");
        try
        {
            w.Write(command);
            w.Write(w.NewLine);
            w.Flush();
            AppendLog("> " + command);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new CeobeException($"命令发送失败: {e.Message}", e);
        }
    }

    /// <summary>优雅停止：stdin 写 stop，宽限期内等待，超时强杀。</summary>
    public void Stop(TimeSpan grace)
    {
        var p = _process;
        if (p is null || !IsAlive)
        {
            Transition(InstanceState.STOPPED, 0);
            return;
        }
        _stopRequested = true;
        Transition(InstanceState.STOPPING, 0);
        try
        {
            SendCommand("stop");
        }
        catch (CeobeException)
        {
            // stdin 可能已关闭，直接走强杀
        }
        try
        {
            if (!p.WaitForExit((int)grace.TotalMilliseconds))
            {
                AppendLog("[Ceobe] 优雅停止超时，强制结束进程");
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { p.WaitForExit(10_000); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    public void Kill()
    {
        var p = _process;
        if (p is not null && !p.HasExited)
        {
            _stopRequested = true;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private void AppendLog(string line)
    {
        lock (_ringLock)
        {
            if (_ring.Count >= RingSize) _ring.Dequeue();
            _ring.Enqueue(line);
        }
        try
        {
            File.AppendAllText(Path.Combine(_dir, "console.log"), line + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 控制台日志落盘失败不阻塞服务
        }
        catch (UnauthorizedAccessException)
        {
        }
        Publish(new InstanceLog(_meta.Name, line));
    }

    private void Transition(InstanceState next, int exitCode)
    {
        if (_state == next && next != InstanceState.STOPPED) return;
        _state = next;
        WriteRuntimeFiles();
        Publish(new StateChanged(_meta.Name, next, exitCode));
    }

    /// <summary>
    /// 落盘 .state / .pid（ADR-007：其他进程据此推断运行态）。
    /// .state 写大写枚举名，须与 Java 版 <c>state.name()</c> 字节一致，否则跨实现读不回。
    /// </summary>
    private void WriteRuntimeFiles()
    {
        try
        {
            File.WriteAllText(Path.Combine(_dir, ".state"), _state.ToString());
            var pidFile = Path.Combine(_dir, ".pid");
            var p = _process;
            if (p is not null && !p.HasExited)
            {
                File.WriteAllText(pidFile, p.Id.ToString());
            }
            else
            {
                if (File.Exists(pidFile)) File.Delete(pidFile);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Publish(IInstanceEvent evt)
    {
        // 单个订阅者异常不影响其他（对齐 Java 侧 catch RuntimeException）
        foreach (var handler in OnEvent?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<IInstanceEvent>)handler)(evt);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void CheckPortFree(int port)
    {
        if (!PortProbe.IsFree(port))
        {
            throw CeobeException.Of("端口 {0} 已被占用，请改用其他端口", port);
        }
    }

    [GeneratedRegex(DonePattern)]
    private static partial Regex DoneRegex();
}
