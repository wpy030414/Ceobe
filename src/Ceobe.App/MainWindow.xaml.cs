using System.Collections.ObjectModel;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Ceobe.App.Controls;
using Ceobe.App.ViewModels;
using Ceobe.App.Views;
using Ceobe.Core.Client;
using Ceobe.Core.Composition;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;

namespace Ceobe.App;

/// <summary>
/// 主窗口 code-behind：只做「接线」——把 XAML 控件接到 ViewModel，把用户手势转成 VM 命令。
/// 一行业务逻辑都不放这里（业务全在 ViewModels，可被单测覆盖，这是闸门 C 的前提）。
///
/// 2026-10-22 客户端编排：外壳不变，`SidebarModeBar` 的服务端/客户端切换只换
/// 左栏数据源、底部动作语义与右侧工作区。这样用户不需要为「管客户端」重新学一遍导航，
/// 实现上也不用开第二个窗口。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly InstanceManager _manager;

    /// <summary>
    /// 本进程的隧道管理器（GUI 即监督进程，对齐 Java 版 CeobeGui.start）。
    /// 必须全程持有引用：它是各实例通道的唯一持有者，被 GC 掉就等于把通道全关了。
    /// </summary>
    private readonly TunnelManager _tunnels;

    /// <summary>客户端进程的持有者。与 _manager 平行：服务端与客户端是两套生命周期。</summary>
    private readonly ClientRuntime _clientRuntime;

    /// <summary>
    /// 内嵌 Web 控制台的持有者。这里只用来「保活」——不持有引用的话，
    /// 监听对象被回收后端口会静默关闭，用户会看到 Web 控制台时好时坏。
    /// </summary>
    private readonly object? _webConsole;

    private bool _windowSized;
    private bool _micaSupported;
    private bool _dialogOpen;
    private bool _shellReady;
    private bool _shellInitialized;
    private ShellMode _mode = ShellMode.Server;
    private InstanceTabs? _workspace;
    private ClientTabs? _clientWorkspace;

    private enum ShellMode
    {
        Server,
        Client,
    }

    /// <summary>侧栏实例列表 VM（服务端模式）。</summary>
    public InstanceListViewModel VmList { get; }

    /// <summary>侧栏客户端列表 VM（客户端模式）。</summary>
    public ClientListViewModel VmClientList { get; }

    /// <summary>
    /// 侧栏导航项集合：服务端与客户端共用一个 NavigationView，
    /// 这里放的是「当前模式可见的行」。
    ///
    /// 为什么不让两个 VM 各挂一个 MenuItemsSource：WinUI 的
    /// <c>NavigationView.MenuItemsSource</c> **从代码里赋值会崩**——
    /// 实测直接 0xC0000005 崩在 <c>set_MenuItemsSource</c> 内部，
    /// 且与时机无关（构造期、Loaded 后、DispatcherQueue 延后都崩）。
    /// XAML 绑定这条路径却是安全的。
    ///
    /// 所以这里的约定是：<c>MenuItemsSource</c> 全程只由 XAML 赋值一次，
    /// 模式切换靠改这个集合的内容（清空 + 重填）完成。
    /// </summary>
    public ObservableCollection<object> NavRows { get; } = [];

    /// <summary>把当前模式的行增量镜像到 <see cref="NavRows"/>。</summary>
    private readonly NavRowMirror _navMirror;

    /// <summary>当前主窗口（文件夹选择器等需要 WindowId 的 API 从这里取）。</summary>
    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Title = "Ceobe";

        _manager = CeobeComposition.NewManager();
        _clientRuntime = new ClientRuntime();

        // GUI 进程本身就要起应用级服务：内嵌 Web 控制台 + 隧道管理器。
        var services = CeobeComposition.StartAppServices(_manager);
        _tunnels = services.Tunnels;
        _webConsole = services.Web;

        VmList = new InstanceListViewModel(_manager);
        VmClientList = new ClientListViewModel(_clientRuntime);
        _navMirror = new NavRowMirror(NavRows);

        // 状态栏跟随当前模式的列表；两个列表都可能从后台线程更新，统一走 PropertyChanged
        VmList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstanceListViewModel.StatusText)) UpdateStatusBar();
        };
        VmClientList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClientListViewModel.StatusText)) UpdateStatusBar();
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _micaSupported = MicaController.IsSupported();
        var startupTheme = ThemeApplier.CurrentKey;
        ApplyBackdrop(startupTheme);
        ConfigureKeyboardAccelerators();

        // 运行期主题预览（设置对话框）也要同步窗口背景。
        ThemeApplier.ThemeChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyBackdrop(ThemeApplier.CurrentKey);
        });

        // 尺寸必须在 XamlRoot 可用后换算（AppWindow 用物理像素，XamlRoot 给缩放比）。
        RootGrid.Loaded += (_, _) => ApplyWindowSizing();

        // 选中变化 → 装配右侧工作区。走 VM 的 SelectionChanged 事件，
        // 不直接读 NavigationView.SelectedItem，保持 View 之间无耦合。
        VmList.SelectionChanged += name => DispatcherQueue.TryEnqueue(() => OnSelectionChanged(name));
        VmClientList.SelectionChanged += name => DispatcherQueue.TryEnqueue(() => OnClientSelectionChanged(name));

        VmList.Refresh();
        VmClientList.Refresh();

        // 后台预热各实例的 Java 要求（冷缓存时 RequiredMajor 只能返回兜底值，
        // 而启动路径是同步的、不能联网，于是「26.3 需要 Java 21」这种误判会让
        // 可用的 JDK 25 被拒绝启动——实测踩过）。
        // 同时预热 Mojang 版本清单：BMCLAPI 首次可能要 4s 才回退直连，提前取好，
        // 新建对话框打开时就能直接命中 30 分钟内存缓存。
        _ = Task.Run(async () =>
        {
            try { await Core.Providers.MojangProvider.ListReleasesAsync(); }
            catch (Exception) { /* 预热失败：对话框会显示错误并允许重试 */ }
        });

        var warmNames = VmList.Rows.Select(r => r.Name).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var n in warmNames)
            {
                try { await Core.Jdk.JavaRequirement.RequiredMajorAsync(_manager.Meta(n).GameVersion); }
                catch (Exception) { /* 预热失败：留给启动路径的兜底处理 */ }
            }
        });

        _shellReady = true;

        // 侧栏与导航的初始化必须等窗口 Loaded：
        // NavigationView 的 MenuItemsSource 在构造期赋值会命中未就绪的模板
        // （实测直接 0xC0000005 崩在 set_MenuItemsSource 里），Loaded 之后才安全。
        RootGrid.Loaded += (_, _) => InitShell();

        Closed += (_, _) =>
        {
            VmList.Detach();
            VmClientList.Detach();
            _workspace?.Detach();
            _clientWorkspace?.Detach();
            Instance = null;
        };
    }

    /// <summary>
    /// 首次就绪后的外壳装配：选定模式、灌入左栏列表、选中第一行。
    /// 只在 Loaded 时跑一次。
    /// </summary>
    private void InitShell()
    {
        if (_shellInitialized) return;
        _shellInitialized = true;

        SidebarModeBar.SelectedItem = SidebarModeBar.Items[0];
        SetMode(ShellMode.Server);
        // 启动即选中第一行。对一个「管服/管客户端」的工具来说，空白面板不提供任何信息。
        if (VmList.SelectedName is null && VmList.Rows.Count > 0)
        {
            VmList.SelectedName = VmList.Rows[0].Name;
        }
    }

    // —— 窗口外观 ——

    /// <summary>
    /// Mica 只用于「跟随系统」：显式浅色/深色时 Mica 的 tint 可能与应用主题不一致
    /// （它跟随系统而不是根元素的 RequestedTheme），此时退回纯色层更可靠。
    /// </summary>
    private void ApplyBackdrop(string themeKey)
    {
        var useMica = _micaSupported && themeKey == "system";
        if (useMica)
        {
            // BaseAlt 是带导航栏应用的官方推荐变体。
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            PageBackground.Visibility = Visibility.Collapsed;
        }
        else
        {
            SystemBackdrop = null;
            PageBackground.Visibility = Visibility.Visible;
        }
    }

    private void ApplyWindowSizing()
    {
        if (_windowSized) return;
        _windowSized = true;

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // AppWindow 的尺寸 API 用物理像素；1000×680 是最小可用逻辑尺寸。
            presenter.PreferredMinimumWidth = (int)(1000 * scale);
            presenter.PreferredMinimumHeight = (int)(680 * scale);
        }

        AppWindow.ResizeClient(new SizeInt32((int)(1280 * scale), (int)(840 * scale)));
    }

    // —— 键盘 ——

    private void ConfigureKeyboardAccelerators()
    {
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, async () =>
        {
            if (_mode == ShellMode.Client) await CreateClientAsync();
            else await CreateInstanceAsync();
        });
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, () =>
        {
            RefreshActiveList();
            return Task.CompletedTask;
        });
        // VK_OEM_COMMA (0xBC)：Windows.System.VirtualKey 没有 Comma 成员。
        AddAccelerator((VirtualKey)0xBC, VirtualKeyModifiers.Control, async () => await OpenSettingsAsync());
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await action();
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    // —— 模式切换 ——

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_shellReady || !_shellInitialized) return;
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        SetMode(tag == "client" ? ShellMode.Client : ShellMode.Server);
    }

    /// <summary>
    /// 切换模式：只换左栏数据源、底部动作与右侧工作区，窗口与导航结构不动。
    /// 切换前先解绑离开的工作区（它持有事件订阅与轮询计时器）。
    /// </summary>
    private void SetMode(ShellMode mode)
    {
        if (!_shellInitialized) return;
        _mode = mode;
        var client = mode == ShellMode.Client;

        _workspace?.Detach();
        _workspace = null;
        _clientWorkspace?.Detach();
        _clientWorkspace = null;

        // 只换集合内容，不碰 Nav.MenuItemsSource（代码赋值那条路会崩）。
        _navMirror.Attach(
            client ? (System.Collections.Specialized.INotifyCollectionChanged)VmClientList.Rows : VmList.Rows,
            client ? VmClientList.Rows.Cast<object>() : VmList.Rows.Cast<object>());
        Nav.SelectedItem = client ? VmClientList.SelectedRow : VmList.SelectedRow;
        CreateButtonText.Text = client ? "新建客户端实例" : "新建实例";
        UpdateStatusBar();
        if (client)
        {
            if (VmClientList.SelectedName is null && VmClientList.Rows.Count > 0)
            {
                // 赋值会触发 SelectionChanged → 装配工作区，不必再手动调一次
                VmClientList.SelectedName = VmClientList.Rows[0].Name;
                return;
            }
            OnClientSelectionChanged(VmClientList.SelectedName);
        }
        else
        {
            OnSelectionChanged(VmList.SelectedName);
        }
    }

    private void UpdateStatusBar()
    {
        StatusText.Text = _mode == ShellMode.Client ? VmClientList.StatusText : VmList.StatusText;
    }

    private void RefreshActiveList()
    {
        if (_mode == ShellMode.Client) VmClientList.Refresh();
        else VmList.Refresh();
        SyncNavSelection();
    }

    /// <summary>
    /// NavigationView 的 SelectedItem 不再用 x:Bind 双向绑定（两种行类型没法共用一个强类型绑定），
    /// 因此列表变化后要显式回写，否则删除/筛选后导航高亮会停在已消失的项上。
    /// </summary>
    private void SyncNavSelection()
    {
        Nav.SelectedItem = _mode == ShellMode.Client ? VmClientList.SelectedRow : VmList.SelectedRow;
    }

    // —— 导航 ——

    /// <summary>
    /// 只有「用户真正选中某个实例」时才写回 SelectedName。
    /// NavigationView 在 MenuItemsSource 变化时会抛 null 的 SelectionChanged；
    /// 把这个 null 回写会清空工作区（旧版退化就发生在这里）。
    /// </summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        switch (args.SelectedItem)
        {
            case InstanceRow row when _mode == ShellMode.Server:
                VmList.SelectedName = row.Name;
                break;
            case ClientRow crow when _mode == ShellMode.Client:
                VmClientList.SelectedName = crow.Name;
                break;
        }
    }

    private async void OnCreateInstanceClick(object sender, RoutedEventArgs e)
    {
        if (_mode == ShellMode.Client) await CreateClientAsync();
        else await CreateInstanceAsync();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e) =>
        await OpenSettingsAsync();

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshActiveList();

    private void OnSelectionChanged(string? name)
    {
        // 先解绑旧工作区：它持有 manager 事件订阅与控制台轮询计时器。
        _workspace?.Detach();
        _workspace = null;

        if (string.IsNullOrEmpty(name))
        {
            ContentHost.Content = CreateEmptyState(client: false);
            return;
        }

        // 工作区（实例头 + SelectorBar + 七个分区）由 InstanceTabs 装配。
        var tabs = new InstanceTabs(_manager, name, _tunnels);
        tabs.InstanceDeleted += deleted =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                OnSelectionChanged(null);
            });
        };
        // 改名后旧名已失效：刷新侧栏并选中新名（会重建工作区）
        tabs.InstanceRenamed += newName =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                VmList.SelectedName = newName;
                SyncNavSelection();
            });
        };
        // 整合包页新建实例成功：刷新侧栏并选中新实例
        tabs.InstanceCreated += created =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmList.Refresh();
                VmList.SelectedName = created;
                SyncNavSelection();
            });
        };
        _workspace = tabs;
        ContentHost.Content = tabs;
    }

    /// <summary>客户端工作区。与服务端工作区同构：实例头 + 分区选择器 + 七个页面。</summary>
    private void OnClientSelectionChanged(string? name)
    {
        _clientWorkspace?.Detach();
        _clientWorkspace = null;

        if (string.IsNullOrEmpty(name))
        {
            ContentHost.Content = CreateEmptyState(client: true);
            return;
        }

        var tabs = new ClientTabs(_clientRuntime, name);
        tabs.InstanceDeleted += _ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VmClientList.Refresh();
                OnClientSelectionChanged(null);
            });
        };
        _clientWorkspace = tabs;
        ContentHost.Content = tabs;
    }

    private EmptyState CreateEmptyState(bool client)
    {
        var newButton = new Button
        {
            Content = client ? "新建客户端实例" : "新建实例",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        newButton.Click += async (_, _) =>
        {
            if (client) await CreateClientAsync();
            else await CreateInstanceAsync();
        };

        return client
            ? new EmptyState
            {
                Glyph = "\uE7FC",
                Title = "还没有客户端实例",
                Description = "创建一个客户端，版本、加载器、Java 与服务器地址会一起配好。",
                ActionContent = newButton,
            }
            : new EmptyState
            {
                Glyph = "\uE7B8",
                Title = "还没有实例",
                Description = "创建一个服务器，几分钟内就能让朋友连进来。",
                ActionContent = newButton,
            };
    }

    // —— 对话框 ——

    private async Task CreateInstanceAsync()
    {
        // 同一时刻只能有一个 ContentDialog；重复点击/快捷键叠加会抛 0x80000019。
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new CreateInstanceDialog(_manager) { XamlRoot = ContentHost.XamlRoot };
            ThemeApplier.ApplyToDialog(dialog);
            await dialog.ShowAsync();
            VmList.Refresh();
            // 新建成功后选中新实例
            if (dialog.CreatedInstance is { } created)
            {
                VmList.SelectedName = created;
                SyncNavSelection();
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task CreateClientAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new CreateClientDialog { XamlRoot = ContentHost.XamlRoot };
            ThemeApplier.ApplyToDialog(dialog);
            await dialog.ShowAsync();
            VmClientList.Refresh();
            if (dialog.CreatedInstance is { } created)
            {
                VmClientList.SelectedName = created;
                SyncNavSelection();
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new SettingsDialog(_manager) { XamlRoot = ContentHost.XamlRoot };
            ThemeApplier.ApplyToDialog(dialog);
            await dialog.ShowAsync();

            // 设置对话框里的主题开关是即时预览；用户如果点了取消，这里把落盘值重新贴回来，
            // 避免「预览改了、没保存、界面却一直是深色」的错觉。
            var theme = Core.Store.AppSettings.Load().Theme;
            ThemeApplier.Apply(this, theme);
            // 数据目录可能已搬走，重新扫描实例列表
            VmList.Refresh();
            VmClientList.Refresh();

            // 搬家后旧工作区的 VM 仍持有旧目录路径，必须重建；清空选中即可。
            if (dialog.HomeMoved)
            {
                VmList.SelectedName = null;
                OnSelectionChanged(null);
                VmClientList.SelectedName = null;
                OnClientSelectionChanged(null);
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
