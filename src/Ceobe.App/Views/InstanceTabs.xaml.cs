using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;

namespace Ceobe.App.Views;

/// <summary>
/// 实例页签容器 code-behind：把选中的实例名注入各页的 ViewModel。
/// 只做接线，业务在各 ViewModel（闸门 C：逻辑可单测，不依赖 XAML）。
/// </summary>
public sealed partial class InstanceTabs : UserControl
{
    private readonly InstanceManager _manager;
    private readonly string _instance;

    /// <summary>
    /// 实例被删除时冒泡到主窗口（主窗口据此刷新侧栏并清空选中）。
    /// View 之间不直接依赖：ConsolePage 触发，本容器转发，MainWindow 订阅。
    /// </summary>
    public event Action<string>? InstanceDeleted;

    public InstanceTabs(InstanceManager manager, string instance)
    {
        InitializeComponent();
        _manager = manager;
        _instance = instance;

        // 控制台页：接 ViewModel 并加载历史日志。
        // 外部托管（CLI start -d）的实例还要按文件偏移增量跟随，由 ConsolePage 内部定时轮询。
        ConsoleTab.Load(new ConsoleViewModel(manager, instance));
        ConsoleTab.InstanceDeleted += name => InstanceDeleted?.Invoke(name);
    }
}
