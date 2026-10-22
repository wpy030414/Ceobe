using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ceobe.App;

/// <summary>
/// 进程入口。
///
/// 普通启动 → WinUI 应用；
/// <c>--supervise &lt;实例&gt;</c> → 无界面的监督进程（ADR-007）：
/// GUI 把实例交给它托管，自己退出后实例仍有控制通道，重启 GUI 可继续 stop / exec / 看日志。
///
/// 之所以用「自身可执行文件 + 隐藏开关」而不是去定位 CLI 产物：
/// 两个二进制在开发机与发布目录里的相对位置不同，自派生不会拿错版本。
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] is "--supervise" or "supervise")
        {
            try
            {
                Environment.ExitCode = Ceobe.Core.Runtime.Supervisor
                    .RunAsync(args[1], foreground: false)
                    .GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // 监督进程没有窗口：错误必须落到 supervisor.log（Launch 已重定向），并给出非零退出码
                Console.Error.WriteLine("[Ceobe] 监督进程启动失败: " + e.Message);
                Environment.ExitCode = 1;
            }
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            App.XamlGeneratedCreateApplicationInstance();
        });
    }
}
