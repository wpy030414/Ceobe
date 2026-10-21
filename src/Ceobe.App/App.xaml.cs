using Microsoft.UI.Xaml;

namespace Ceobe.App;

/// <summary>
/// WinUI 3 应用入口。本进程即所启动实例的「监督进程」（含控制通道与隧道管理），
/// 对齐 Java 版 CeobeGui。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// 关于 Windows App SDK 引导：**本工程不调用 Bootstrap.Initialize**。
    ///
    /// 探针实测 + 配置推导：本工程是 <c>SelfContained=true</c> + <c>WindowsPackageType=None</c>
    /// （非打包分发，免 MSIX 旁加载与证书）。这种组合下 WindowsAppSDK 是自包含的，
    /// 运行时随发布产物一起带出并由框架自动初始化；再手动 Bootstrap.Initialize
    /// 属于双重初始化，反而可能失败。
    /// 手动引导只在「框架依赖 + 非打包」时才需要——如果将来改回框架依赖发布，
    /// 需按 <c>Bootstrap.TryInitialize(uint, out int hresult)</c>（返回 bool，第二参是 HRESULT）
    /// 的真实签名补上，并注意句柄要存活到进程结束。
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 应用设置里的数据目录（整改项 3）必须在任何实例操作之前生效
        Core.Store.AppSettings.Load().Apply();

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception e)
        {
            // 窗口构造失败（XAML 资源缺失、Converter 未注册等）：落盘可诊断日志后退出，
            // 不弹 WinForms 对话框——本工程没引用它，且弹窗本身也可能因同样原因失败。
            WriteCrashLog("窗口初始化失败", e);
            Exit();
        }
    }

    /// <summary>
    /// 未捕获异常兜底。WinUI 3 默认直接崩溃退出且不留痕迹，
    /// 对一个管着 Minecraft 服务端的工具来说，「GUI 崩了但服还在跑」需要能被诊断。
    /// 不吞异常：让服务端进程与 GUI 状态保持一致（GUI 死了实例仍在，用户可用 CLI 接管）。
    ///
    /// 参数类型必须写全 <c>Microsoft.UI.Xaml.UnhandledExceptionEventArgs</c>：
    /// <c>System.UnhandledExceptionEventArgs</c> 同名，不写全会撞 CS0104 歧义。
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
        WriteCrashLog("未处理异常", e.Exception);

    /// <summary>把崩溃信息写进数据目录，供事后排查（写不了就静默，不掩盖原异常）。</summary>
    private static void WriteCrashLog(string title, Exception e)
    {
        try
        {
            var dir = Core.Store.CeobeHome.Ensure(Core.Store.CeobeHome.Root());
            File.AppendAllText(Path.Combine(dir, "gui-crash.log"),
                $"[{DateTimeOffset.Now:O}] {title}: {e}\n\n", new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 连崩溃日志都写不了（磁盘满/权限），此时无法再做更多
        }
    }
}
