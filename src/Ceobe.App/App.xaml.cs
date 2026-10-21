using Microsoft.UI.Xaml;

namespace Ceobe.App;

/// <summary>
/// WinUI 3 应用入口。
/// 本进程即所启动实例的「监督进程」（含控制通道与隧道管理），对齐 Java 版 CeobeGui。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
