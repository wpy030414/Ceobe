using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Event;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;

namespace Ceobe.App.Views;

/// <summary>
/// 实例页签容器 code-behind：把选中的实例名注入各页的 ViewModel。
/// 只做接线，业务在各 ViewModel（闸门 C：逻辑可单测，不依赖 XAML）。
///
/// 七页齐全（对齐 Java 版 TabPane）：控制台 / 服务器配置 / 游戏配置 / Mod / 整合包 / 玩家 / 穿透。
/// 后三页对应训练员反馈的整改项 2/1 与整合包装载路径，此前一直缺接线。
/// </summary>
public sealed partial class InstanceTabs : UserControl
{
    private readonly InstanceManager _manager;
    private readonly string _instance;
    private readonly ConsoleViewModel _console;
    private readonly TunnelViewModel _tunnel;
    private readonly Action<IInstanceEvent> _onManagerEvent;

    /// <summary>
    /// 实例被删除时冒泡到主窗口（主窗口据此刷新侧栏并清空选中）。
    /// View 之间不直接依赖：ConsolePage 触发，本容器转发，MainWindow 订阅。
    /// </summary>
    public event Action<string>? InstanceDeleted;

    /// <summary>
    /// 实例被改名时冒泡到主窗口（页签标题与侧栏项都是实例名，改了必须重建）。
    /// </summary>
    public event Action<string>? InstanceRenamed;

    /// <summary>
    /// 整合包页「新建并安装」成功后冒泡：新实例需要立刻出现在侧栏并被选中，
    /// 否则用户会以为新建失败（当前页还停留在旧实例上）。
    /// </summary>
    public event Action<string>? InstanceCreated;

    /// <param name="tunnels">
    /// 本进程的隧道管理器（GUI 即监督进程，见 CeobeComposition.StartAppServices）。
    /// 为 null 时穿透页退化为「只能经控制通道操作外部托管的实例」。
    /// </param>
    public InstanceTabs(InstanceManager manager, string instance, TunnelManager? tunnels = null)
    {
        InitializeComponent();
        _manager = manager;
        _instance = instance;

        // 控制台页：接 ViewModel 并加载历史日志。
        // 外部托管（CLI start -d）的实例还要按文件偏移增量跟随，由 ConsolePage 内部定时轮询。
        _console = new ConsoleViewModel(manager, instance);
        ConsoleTab.Load(_console);
        ConsoleTab.InstanceDeleted += name => InstanceDeleted?.Invoke(name);

        // 服务器配置页：改名成功后冒泡，让主窗口重建页签（标题即实例名）
        ServerTab.Load(new ServerViewModel(manager, instance));
        ServerTab.InstanceRenamed += name => InstanceRenamed?.Invoke(name);

        // 游戏配置页（整改项 5：三级视图）
        GameTab.Load(new GameConfigViewModel(manager, instance));

        // Mod 页：上半 Modrinth 搜索装卸，下半整改项 4 一键导入（两个独立 VM）
        ModsTab.Load(new ModsViewModel(manager, instance), new ImportViewModel(manager, instance));

        // 整合包页：两条路径（装到本实例 / 按包新建实例）
        ModpacksTab.Load(new ModpacksViewModel(manager, instance));
        ModpacksTab.InstanceCreated += name => InstanceCreated?.Invoke(name);

        // 玩家页（整改项 2：白名单/OP/封禁的图形入口）
        PlayersTab.Load(new PlayerListViewModel(manager, instance));

        // 穿透页（整改项 1：可达性三态 + 绑定冲突预检）
        _tunnel = new TunnelViewModel(manager, instance, tunnels);
        TunnelTab.Load(_tunnel);

        // 实例停止时穿透页的卡片要跟着复位——否则界面上还挂着一条早已失效的通道地址，
        // 用户会照着它去分享。订阅必须成对解除，见 OnUnloaded。
        _onManagerEvent = evt => _tunnel.HandleEvent(evt);
        _manager.OnEvent += _onManagerEvent;

        // 页签切回时重读磁盘：名单/整合包/通道状态都可能已被别处改动
        Tabs.SelectionChanged += OnTabSelectionChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 切到某个页签时让它自己刷新一次。只读页实现 <see cref="IReloadable"/>；
    /// 带未保存编辑的页（服务器配置/游戏配置）刻意不实现，避免冲掉用户正在改的内容。
    /// </summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Content: IReloadable reloadable })
        {
            reloadable.Reload();
        }
    }

    /// <summary>
    /// 容器被替换（切换实例）时解绑事件源。
    /// 不解绑的后果：每切一次实例就多一个永不释放的订阅，旧 VM 还会继续被写入日志行。
    /// </summary>
    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _manager.OnEvent -= _onManagerEvent;
        _console.Detach();
        Unloaded -= OnUnloaded;
    }
}
