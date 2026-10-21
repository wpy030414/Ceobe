using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Event;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;

namespace Ceobe.App.Views;

/// <summary>
/// 实例工作区：实例头 + SelectorBar + 七个分区页面。
/// 只做接线——业务在各 ViewModel（闸门 C）。
///
/// 2026-10-22 Fluent 2 重构：TabView → SelectorBar；启停/删除从控制台页移到实例头，
/// 使任意分区都能直接启停；页面全部同层声明，当前页可见并填满 * 行。
/// </summary>
public sealed partial class InstanceTabs : UserControl
{
    private readonly InstanceManager _manager;
    private readonly string _instance;
    private readonly ConsoleViewModel _console;
    private readonly TunnelViewModel _tunnel;
    private readonly Action<IInstanceEvent> _onManagerEvent;
    private bool _detached;

    /// <summary>实例被删除时冒泡到主窗口（主窗口据此刷新侧栏并清空选中）。</summary>
    public event Action<string>? InstanceDeleted;

    /// <summary>实例被改名时冒泡到主窗口（页签标题与侧栏项都是实例名，改了必须重建）。</summary>
    public event Action<string>? InstanceRenamed;

    /// <summary>整合包页「新建并安装」成功后冒泡：新实例需要立刻出现在侧栏并被选中。</summary>
    public event Action<string>? InstanceCreated;

    /// <summary>实例名（SelectorBar 上方的标题）。</summary>
    public string InstanceName => _instance;

    /// <summary>控制台 VM：实例头的状态点、启停按钮与删除动作都绑它。</summary>
    public ConsoleViewModel Console => _console;

    public InstanceTabs(InstanceManager manager, string instance, TunnelManager? tunnels = null)
    {
        _manager = manager;
        _instance = instance;

        // 必须在 InitializeComponent 之前建好：XAML 里的 x:Bind 在初始化时就会求值 Console。
        _console = new ConsoleViewModel(manager, instance);

        InitializeComponent();

        // 控制台页：接 ViewModel 并加载历史日志。
        ConsoleTab.Load(_console);

        // 服务器配置页：改名成功后冒泡，让主窗口重建页签（标题即实例名）
        ServerTab.Load(new ServerViewModel(manager, instance));
        ServerTab.InstanceRenamed += name => InstanceRenamed?.Invoke(name);

        // 游戏配置页（整改项 5：三级视图 + 资源包/数据包复合控件）
        GameTab.Load(new GameConfigViewModel(manager, instance));

        // Mod 页：Modrinth 搜索装卸 + 一键导入（两个独立 VM）
        ModsTab.Load(new ModsViewModel(manager, instance), new ImportViewModel(manager, instance));

        // 整合包页：两条路径（装到本实例 / 按包新建实例）
        ModpacksTab.Load(new ModpacksViewModel(manager, instance));
        ModpacksTab.InstanceCreated += name => InstanceCreated?.Invoke(name);

        // 玩家页（整改项 2：白名单/OP/封禁的图形入口）
        PlayersTab.Load(new PlayerListViewModel(manager, instance));

        // 穿透页（整改项 1：可达性三态 + 绑定冲突预检）
        _tunnel = new TunnelViewModel(manager, instance, tunnels);
        TunnelTab.Load(_tunnel);

        // 实例停止时穿透页的卡片要跟着复位——否则界面上还挂着一条早已失效的通道地址。
        // 只转发状态/通道事件：日志事件由 ConsoleViewModel 自己消费，穿透页不关心。
        _onManagerEvent = evt =>
        {
            if (evt is StateChanged or TunnelChanged) _tunnel.HandleEvent(evt);
        };
        _manager.OnEvent += _onManagerEvent;

        // 默认落在控制台（使用频率最高，也对齐旧版启动行为）
        SectionBar.SelectedItem = SectionBar.Items[0];

        Unloaded += OnUnloaded;
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index < 0) return;

        UserControl[] pages =
        [
            ConsoleTab, ServerTab, GameTab, ModsTab, ModpacksTab, PlayersTab, TunnelTab,
        ];

        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        // 切回只读页时重读磁盘：名单/整合包/通道状态可能已被别处改动。
        // 带未保存编辑的页（服务器配置/游戏配置）刻意不实现 IReloadable。
        if (pages[index] is IReloadable reloadable) reloadable.Reload();

        // 游戏配置的字段很长，切回来时回到顶部（上次的滚动位置不应成为第一眼）。
        if (pages[index] is GameConfigPage gameConfig) gameConfig.ResetScroll();
    }

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_console.IsLive)
        {
            await _console.StopCommand.ExecuteAsync(null);
            return;
        }

        await _console.StartCommand.ExecuteAsync(null);
        // 启动成功后本进程接管日志事件流，文件轮询必须停掉。
        ConsoleTab.StopPolling();
    }

    /// <summary>
    /// 删除确认：三选对话框（仅移除 / 连数据删除 / 取消）。
    /// 不可逆操作必须在 View 层确认，VM 只接受已决定好的 purge 模式。
    /// </summary>
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "删除实例",
            Content = $"删除实例「{_instance}」？\n\n" +
                      "「仅移除」保留世界数据文件；「连数据删除」将永久删除整个实例目录，不可恢复。",
            PrimaryButtonText = "仅移除",
            SecondaryButtonText = "连数据删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);

        var result = await dialog.ShowAsync();
        switch (result)
        {
            case ContentDialogResult.Primary:
                _console.DeleteCommand.Execute(false);
                break;
            case ContentDialogResult.Secondary:
                _console.DeleteCommand.Execute(true);
                break;
            default:
                return;
        }

        if (_console.Deleted) InstanceDeleted?.Invoke(_instance);
    }

    /// <summary>
    /// 解绑本轮工作区持有的事件订阅与轮询计时器。幂等；主窗口替换工作区前会显式调用，
    /// Unloaded 也会兜底调用（两者不重复执行）。
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;

        _manager.OnEvent -= _onManagerEvent;
        _console.Detach();
        ConsoleTab.StopPolling();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();
}
