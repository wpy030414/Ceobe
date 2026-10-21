using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.App.Views;
using Ceobe.Core.Composition;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;

namespace Ceobe.App;

/// <summary>
/// 主窗口 code-behind。只做「接线」——把 XAML 控件接到 ViewModel，把用户手势转成 VM 命令；
/// 一行业务逻辑都不放这里（业务全在 ViewModels，可被单测覆盖，这是闸门 C 的前提）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly InstanceManager _manager;

    /// <summary>
    /// 本进程的隧道管理器（GUI 即监督进程，对齐 Java 版 CeobeGui.start）。
    /// 必须全程持有引用：它是各实例通道的唯一持有者，被 GC 掉就等于把通道全关了。
    /// </summary>
    private readonly TunnelManager _tunnels;

    /// <summary>
    /// 内嵌 Web 控制台的持有者。这里只用来「保活」——不持有引用的话，
    /// 监听对象被回收后端口会静默关闭，用户会看到 Web 控制台时好时坏。
    /// 类型不写死是因为 WebConsole 的命名空间与类同名，写全名很难读。
    /// </summary>
    private readonly object? _webConsole;

    /// <summary>侧栏实例列表 VM（XAML 以 x:Bind 绑定工具条与状态栏）。</summary>
    public InstanceListViewModel VmList { get; }

    /// <summary>实例列表 UserControl（构造器注入 VM，故由代码而非 XAML 实例化）。</summary>
    private readonly Views.InstanceListView _listView;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Ceobe：Minecraft 私有服务器编排引擎";

        _manager = CeobeComposition.NewManager();

        // GUI 进程本身就要起应用级服务：内嵌 Web 控制台 + 隧道管理器。
        // 少了这一步，穿透页只能靠控制通道操作外部托管的实例，GUI 自己启的服反而开不了通道。
        var services = CeobeComposition.StartAppServices(_manager);
        _tunnels = services.Tunnels;
        _webConsole = services.Web;

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

        // 启动即选中第一个实例。对一个「管服」工具来说，空白面板不提供任何信息；
        // 直接落到第一个服的控制台，用户一眼就能看出它是活的还是停的。
        // 必须在上面那次 OnSelectionChanged(null) 之后设，否则会被它顶掉。
        if (VmList.SelectedName is null && VmList.Rows.Count > 0)
        {
            VmList.SelectedName = VmList.Rows[0].Name;
        }
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
        // 页签内容由 InstanceTabs 装配（控制台/服务器配置/游戏配置/Mod/整合包/玩家/穿透）
        var tabs = new InstanceTabs(_manager, name, _tunnels);
        tabs.InstanceDeleted += deleted =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                OnSelectionChanged(null);
            });
        };
        // 改名后旧名已失效：刷新侧栏并选中新名（会重建页签）
        tabs.InstanceRenamed += newName =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                VmList.SelectedName = newName;   // 触发 SelectionChanged → OnSelectionChanged 重建
            });
        };
        // 整合包页新建实例成功：刷新侧栏并选中新实例（会触发页签重建）
        tabs.InstanceCreated += created =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                VmList.SelectedName = created;
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
        // 设置对话框里的主题开关是即时预览；用户如果点了取消，这里把落盘值重新贴回来，
        // 避免「预览改了、没保存、界面却一直是深色」的错觉。
        ThemeApplier.Apply(this, Core.Store.AppSettings.Load().Theme);
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
