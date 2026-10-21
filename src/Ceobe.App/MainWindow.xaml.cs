using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.App.Views;
using Ceobe.Core.Composition;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App;

/// <summary>
/// 主窗口 code-behind。只做「接线」——把 XAML 控件接到 ViewModel，把用户手势转成 VM 命令；
/// 一行业务逻辑都不放这里（业务全在 ViewModels，可被单测覆盖，这是闸门 C 的前提）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly InstanceManager _manager;

    /// <summary>侧栏实例列表 VM（XAML 以 x:Bind 绑定工具条与状态栏）。</summary>
    public InstanceListViewModel VmList { get; }

    /// <summary>实例列表 UserControl（构造器注入 VM，故由代码而非 XAML 实例化）。</summary>
    private readonly Views.InstanceListView _listView;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Ceobe：Minecraft 私有服务器编排引擎";

        _manager = CeobeComposition.NewManager();
        VmList = new InstanceListViewModel(_manager);

        // 列表控件构造器注入 VM（保住依赖注入，不用无参构造 + 后设属性）
        _listView = new Views.InstanceListView(VmList);
        ListHost.Content = _listView;

        // 选中变化 → 装配右侧页签。走 VM 的 SelectionChanged 事件，
        // 不直接读 ListView.SelectedItem，避免 View 之间耦合。
        VmList.SelectionChanged += name => DispatcherQueue.TryEnqueue(() => OnSelectionChanged(name));
        VmList.Refresh();

        // 实例状态变化（启动/停止/崩溃）时刷新侧栏徽标
        _manager.OnEvent += _ => DispatcherQueue.TryEnqueue(VmList.Refresh);

        OnSelectionChanged(null);
    }

    private void OnSelectionChanged(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            TopTitle.Text = "未选择实例";
            ContentHost.Content = new EmptyState();
            return;
        }
        TopTitle.Text = name;
        // 页签内容由 InstanceTabs 装配（控制台/配置/Mod/整合包/玩家/穿透）
        var tabs = new InstanceTabs(_manager, name);
        tabs.InstanceDeleted += deleted =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                OnSelectionChanged(null);
            });
        };
        ContentHost.Content = tabs;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => VmList.Refresh();

    private async void OnCreateInstance(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateInstanceDialog(_manager) { XamlRoot = ContentHost.XamlRoot };
        await dialog.ShowAsync();
        VmList.Refresh();
        // 新建成功后选中新实例
        if (dialog.CreatedInstance is { } created)
        {
            VmList.SelectedName = created;
        }
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_manager) { XamlRoot = ContentHost.XamlRoot };
        await dialog.ShowAsync();
        // 数据目录可能已搬走，重新扫描实例列表
        VmList.Refresh();
    }

    /// <summary>未选实例时的占位内容。</summary>
    private sealed class EmptyState : Grid
    {
        public EmptyState()
        {
            Children.Add(new TextBlock
            {
                Text = "从左侧选择一个实例，或点「新建实例」开一个服",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 0xA1, 0xA1, 0xAA)),
            });
        }
    }
}
