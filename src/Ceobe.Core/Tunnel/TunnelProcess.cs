using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// 隧道子进程通用工具：启动、逐行日志泵（同时落盘 tunnel.log）、就绪等待、终止。
/// 移植自 Java <c>tunnel/TunnelProcess.java</c>。
///
/// 与 Java 的语义差异（都只影响等待方式，判定条件与 45 秒超时完全一致）：
/// <list type="bullet">
/// <item>Java 用 250ms 轮询 <c>ready.get()</c>；这里由日志泵在解析出新地址时点亮
///   <see cref="TaskCompletionSource{TResult}"/>，不再空转。泵线程与 Java 一样**不因就绪而提前退出**，
///   继续把后续诊断行写进 tunnel.log（连接被重置一类的关键证据就在这些行里）。</item>
/// <item>Java 的 <c>redirectErrorStream(true)</c> 在 .NET 无等价开关，改为 stdout/stderr 两路
///   泵入同一个单消费者队列（<see cref="Channel{T}"/>），保持「逐行单写者」的顺序语义。</item>
/// <item>进程退出后先让队列排空再判「提前退出」，避免最后一行（往往正是地址行）被漏读。</item>
/// </list>
/// </summary>
internal static class TunnelProcess
{
    /// <summary>stdout/stderr 行日志的落盘文件名（实例目录下）。</summary>
    internal const string LogFileName = "tunnel.log";

    /// <summary>
    /// 启动子进程并等待就绪。成功返回即 <paramref name="ready"/> 给出非空值；
    /// 进程提前退出或超时会终止进程并抛 <see cref="CeobeException"/>。
    /// </summary>
    /// <param name="psi">已配置好的进程启动信息</param>
    /// <param name="instanceDir">实例目录（tunnel.log 落盘处）</param>
    /// <param name="logSink">逐行诊断（带 [tunnel] 前缀）</param>
    /// <param name="lineParser">原始行解析器（用于提取公网地址）</param>
    /// <param name="ready">就绪判据：返回非空即就绪</param>
    /// <param name="timeout">就绪超时</param>
    /// <param name="session">承载子进程与公网地址的会话</param>
    /// <param name="ct">取消令牌（取消按「超时未就绪」同路径终止进程）</param>
    internal static async Task RunAsync(ProcessStartInfo psi, string instanceDir,
        Action<string> logSink, Action<string> lineParser, Func<string?> ready,
        TimeSpan timeout, TunnelSession session, CancellationToken ct)
    {
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new CeobeException("无法启动隧道进程: 进程未能创建");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            throw new CeobeException("无法启动隧道进程: " + e.Message, e);
        }
        session.Process = p;

        var logFile = Path.Combine(instanceDir, LogFileName);
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 单消费者泵：与 Java 的常驻虚拟线程同构——不因就绪而退出，持续泵到 EOF。
        var pump = Task.Run(async () =>
        {
            var notified = false;
            try
            {
                await foreach (var line in channel.Reader.ReadAllAsync(CancellationToken.None))
                {
                    Emit(logSink, logFile, lineParser, line);
                    if (!notified)
                    {
                        var url = ready();
                        if (url is not null)
                        {
                            notified = true;
                            readyTcs.TrySetResult(url);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 泵内任何异常都不允许冒泡影响主流程（对齐 Java 的 catch(ignored)）
            }
        }, CancellationToken.None);

        var stdout = PumpAsync(p.StandardOutput, channel.Writer);
        var stderr = PumpAsync(p.StandardError, channel.Writer);
        _ = Task.WhenAll(stdout, stderr).ContinueWith(_ => channel.Writer.TryComplete());

        if (ready() is not null) return; // 极端竞态：启动瞬间地址已可判

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var exitWait = WaitExitAsync(p, cts.Token);
        var winner = await Task.WhenAny(readyTcs.Task, exitWait).ConfigureAwait(false);

        if (winner == readyTcs.Task)
        {
            // 就绪：子进程继续存活并持续泵日志（正是期望形态），本方法立即返回。
            return;
        }

        // 进程退出（或超时后流被关闭）：等两路读取与消费队列排空（流随进程退出关闭，正常会很快结束）。
        await WhenWithGrace(TimeSpan.FromSeconds(3), stdout, stderr, pump).ConfigureAwait(false);
        if (ready() is not null)
        {
            // 最后一行正是地址行：进程虽退但已就绪（保守放行，交由公网侧探测判定通道死活）。
            return;
        }
        var code = SafeExitCode(p);
        ForceKill(p);
        DisposeQuietly(p);
        ct.ThrowIfCancellationRequested();
        if (cts.IsCancellationRequested)
        {
            throw CeobeException.Of("隧道在 {0:0} 秒内未就绪，请查看 {1}",
                timeout.TotalSeconds, LogFileName);
        }
        throw CeobeException.Of("隧道进程提前退出（退出码 {0}），请查看 {1}", code, LogFileName);
    }

    private static void Emit(Action<string> logSink, string logFile, Action<string> lineParser, string line)
    {
        try
        {
            logSink("[tunnel] " + line);
        }
        catch (Exception)
        {
            // 日志订阅者异常不影响隧道
        }
        try
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // 落盘失败静默（对齐 Java）
        }
        try
        {
            lineParser(line);
        }
        catch (Exception)
        {
            // 解析异常等价于「这行没有地址」
        }
    }

    private static async Task PumpAsync(StreamReader reader, ChannelWriter<string> writer)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 进程退出会中断读取，属正常路径
        }
    }

    private static async Task WaitExitAsync(Process p, CancellationToken ct)
    {
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 由超时/取消路径统一处理
        }
        catch (Exception)
        {
            // 等待句柄异常不致命：后续以 HasExited 为准
        }
    }

    private static async Task WhenWithGrace(TimeSpan grace, params Task[] tasks)
    {
        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all, Task.Delay(grace)).ConfigureAwait(false);
    }

    private static int SafeExitCode(Process p)
    {
        try
        {
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// 温和终止（对齐 Java <c>destroy()</c> 语义），5 秒未退再强杀整棵进程树。
    /// Windows 下无「温和信号」，<c>Kill(false)</c> 即 TerminateProcess，故再兜底 <c>Kill(true)</c> 清子进程。
    /// </summary>
    internal static void Terminate(TunnelSession session)
    {
        var p = session.Process;
        if (p is null) return;
        session.Process = null;
        try
        {
            if (p.HasExited) return;
            try
            {
                p.Kill(entireProcessTree: false);
            }
            catch (Exception)
            {
                // 已进入退出流程
            }
            try
            {
                if (!p.WaitForExit(5000)) p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 忽略
            }
        }
        catch (Exception)
        {
            // 进程已不可用
        }
        finally
        {
            DisposeQuietly(p);
        }
    }

    private static void ForceKill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 已退出
        }
    }

    private static void DisposeQuietly(object disposable)
    {
        try
        {
            ((IDisposable?)disposable)?.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>
    /// 在 PATH 中查找可执行文件（Windows 兼容 .exe/.cmd/.bat）。移植自 Java <c>which</c>。
    /// 找不到返回 null。internal 可见性：Tailscale provider 与单测需要。
    /// </summary>
    internal static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim(), name + ext);
                }
                catch (ArgumentException)
                {
                    continue; // PATH 中的非法目录片段
                }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
