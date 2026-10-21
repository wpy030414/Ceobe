using Ceobe.Core.Composition;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// 实例监督进程：托管一个 MC 服务端 + 开放控制通道（ADR-007）。
///
/// <c>ceobe start -d</c> 以独立进程重新拉起本可执行文件并传入 <c>supervise</c> 子命令；
/// 前台模式则由 <c>start</c> 直接调用 <see cref="RunAsync"/>。
/// 之所以是「监督进程」而非全局 daemon：一个实例一个进程，实例停了进程就退，
/// 不留常驻服务，也就不需要开机自启与权限提升。
/// </summary>
public static class Supervisor
{
    /// <summary>阻塞运行直至实例停止；返回进程退出码。</summary>
    public static async Task<int> RunAsync(string name, bool foreground, CancellationToken ct = default)
    {
        var manager = CoreWiring.NewManager();
        var meta = manager.Meta(name);
        var dir = manager.Dir(name);

        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnEvent += evt =>
        {
            if (evt.Instance != name) return;
            switch (evt)
            {
                case InstanceLog log when foreground:
                    Console.WriteLine(log.Line);
                    break;
                case StateChanged sc when foreground:
                    Console.WriteLine($"[ceobe] {name} -> {sc.State.ZhLabel()}");
                    break;
                case TunnelChanged tc when foreground:
                    Console.WriteLine($"[ceobe] {tc.Kind} 地址: {tc.PublicUrl}");
                    break;
            }
            if (evt is StateChanged s
                && (s.State == InstanceState.STOPPED || s.State == InstanceState.CRASHED))
            {
                finished.TrySetResult(s.State == InstanceState.CRASHED ? s.ExitCode : 0);
            }
        };

        // 装配走 Core 的 composition root：GUI 进程本身也是监督进程，
        // 两边必须用同一套接线逻辑，否则「CLI 起得来 GUI 起不来」这类偏差查不出来。
        // 隧道依赖 IInstanceHost=manager 故内部后建；控制通道再把两者一起接上
        // （避免 runtime ↔ tunnel 循环依赖，见 InstanceManager.OpenControl）。
        var (tunnels, web) = CeobeComposition.StartAppServices(manager);
        await using var _web = web;

        if (foreground)
        {
            // Web 控制台可能因端口被占而起不来（StartAppServices 返回 null），不能盲解引用
            Console.WriteLine(web is null
                ? "[ceobe] Web 控制台未能启动（端口被占？）；不影响实例运行"
                : $"[ceobe] Web 控制台: {web.BaseUrl}");
        }

        // 进程退出前优雅停止服务端：否则关掉终端会留下孤儿 MC 进程继续占端口
        using var hook = RegisterShutdownHook(manager, name, foreground);

        try
        {
            manager.Start(name);
        }
        catch (CeobeException e)
        {
            // 启动失败（缺 JDK、端口占用、jar 不存在）必须可见：
            // Java 版曾因异常只写进 GUI 文本框而表现为「静默失败」（ADR-018）
            Console.Error.WriteLine($"[ceobe] 启动失败: {e.Message}");
            tunnels.Close();
            return 1;
        }

        // ⚠ 控制通道必须在 Start 之后开：OpenControl 取的是「本进程托管的进程句柄」，
        // 启动前它还是 null，会直接抛「实例 X 未在本进程运行」——`ceobe start -d` 曾因此
        // 一启动就死，supervisor.log 里只有这一行错误。
        await using var control = manager.OpenControl(name, tunnels);

        if (foreground)
        {
            // 前台：Ctrl+C 时优雅停止而不是直接杀
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("[ceobe] 收到中断，正在优雅停止…");
                try { manager.Stop(name); } catch (CeobeException) { }
            };
        }

        using var reg = ct.Register(() =>
        {
            try { manager.Stop(name); } catch (CeobeException) { }
            finished.TrySetResult(0);
        });

        var code = await finished.Task.ConfigureAwait(false);
        tunnels.Close();
        return code;
    }

    private static IDisposable RegisterShutdownHook(InstanceManager manager, string name, bool foreground) =>
        new ShutdownHook(() =>
        {
            var p = manager.ProcessOf(name);
            if (p is null || !p.IsAlive) return;
            if (foreground) Console.WriteLine("[ceobe] 进程退出，正在优雅停止服务端…");
            try
            {
                p.Stop(TimeSpan.FromSeconds(90));
            }
            catch (Exception)
            {
                // 退出钩子里抛异常会让进程以非预期方式终止，吞掉
            }
        });

    /// <summary>
    /// 进程退出钩子。.NET 没有 Java 的 addShutdownHook，用 AppDomain.ProcessExit +
    /// 注册/注销配对实现；Dispose 负责注销，避免钩子在正常退出路径上重复触发。
    /// </summary>
    private sealed class ShutdownHook : IDisposable
    {
        private readonly Action _action;
        private int _done;

        public ShutdownHook(Action action)
        {
            _action = action;
            AppDomain.CurrentDomain.ProcessExit += OnExit;
        }

        private void OnExit(object? sender, EventArgs e) => Run();

        private void Run()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            _action();
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            Run();
        }
    }
}
