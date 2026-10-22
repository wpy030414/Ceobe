using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Client;

namespace Ceobe.App.Views;

/// <summary>
/// 客户端实例工作区：实例头 + SelectorBar + 七个分区页面。
/// 只做接线——业务在各 ViewModel（闸门 C）。
///
/// 页面全部同层声明、仅当前可见：Collapsed 的页面不参与布局，当前页自然填满 * 行
/// （与服务端 InstanceTabs 同一套取舍，避免父子双滚动）。
/// </summary>
public sealed partial class ClientTabs : UserControl
{
    private readonly ClientRuntime _runtime;
    private readonly string _instance;
    private readonly ClientMeta _meta;
    private readonly ClientModsViewModel _mods;
    private readonly ClientPacksViewModel _packs;
    private readonly ClientSavesViewModel _saves;
    private readonly ClientServersViewModel _servers;
    private readonly ClientLogViewModel _log;
    private readonly ClientSettingsViewModel _settings;
    private bool _detached;

    /// <summary>实例被删除时冒泡到主窗口（主窗口据此刷新侧栏并清空选中）。</summary>
    public event Action<string>? InstanceDeleted;

    /// <summary>实例名（SelectorBar 上方的标题）。</summary>
    public string InstanceName => _instance;

    /// <summary>启动页 VM：实例头的状态文字、启停按钮都绑它。</summary>
    public ClientLaunchViewModel LaunchVm { get; }

    public ClientTabs(ClientRuntime runtime, string name)
    {
        _runtime = runtime;
        _instance = name;
        _meta = ClientStore.Require(name);

        // 必须在 InitializeComponent 之前建好：XAML 的 x:Bind 在初始化时就会求值 LaunchVm
        LaunchVm = new ClientLaunchViewModel(runtime, _meta);
        InitializeComponent();

        LaunchTab.Load(LaunchVm);

        _mods = new ClientModsViewModel(_meta);
        ModsTab.Load(_mods);

        _packs = new ClientPacksViewModel(_meta);
        PacksTab.Load(_packs);

        _saves = new ClientSavesViewModel(runtime, _meta);
        SavesTab.Load(_saves);

        _servers = new ClientServersViewModel(_meta);
        ServersTab.Load(_servers);
        // 「加入服务器」= 切回启动页并带 --server 起游戏：这是客户端编排的主路径
        _servers.JoinRequested += address =>
        {
            SectionBar.SelectedItem = SectionBar.Items[0];
            LaunchVm.LaunchCommand.Execute(address);
        };

        _log = new ClientLogViewModel(runtime, _meta);
        LogTab.Load(_log);

        _settings = new ClientSettingsViewModel(_meta);
        SettingsTab.Load(_settings);

        // 默认落在启动页（唯一主行动的所在地）
        SectionBar.SelectedItem = SectionBar.Items[0];
        Unloaded += (_, _) => Detach();
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index < 0) return;

        UserControl[] pages =
        [
            LaunchTab, ModsTab, PacksTab, SavesTab, ServersTab, LogTab, SettingsTab,
        ];
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        // 切回任一分区时重读磁盘：mods / 存档 / options.txt 都可能被游戏或用户改过。
        // 启动页不重读安装状态以外的东西（它自己订阅了运行时事件）。
        switch (pages[index])
        {
            case ClientLaunchPage: LaunchVm.Reload(); break;
            case ClientModsPage: _mods.Reload(); break;
            case ClientPacksPage: _packs.Reload(); break;
            case ClientSavesPage: _saves.Reload(); break;
            case ClientServersPage: _servers.Reload(); break;
            case ClientLogPage: _log.Reload(); break;
            case ClientSettingsPage: _settings.Reload(); break;
        }
    }

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (LaunchVm.IsLive)
        {
            await LaunchVm.StopCommand.ExecuteAsync(null);
            return;
        }
        await LaunchVm.LaunchCommand.ExecuteAsync(null);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ClientStore.Dir(_instance))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 打不开资源管理器不影响任何功能
        }
    }

    private async void OnKill(object sender, RoutedEventArgs e)
    {
        if (!_runtime.IsRunning(_instance)) return;
        var dialog = new ContentDialog
        {
            Title = "强制结束游戏",
            Content = "这会直接终止 Java 进程，未保存的进度会丢失。\n\n"
                      + "正常情况下请用「停止游戏」——游戏会自己存盘退出。",
            PrimaryButtonText = "强制结束",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _runtime.Kill(_instance);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsRunning(_instance))
        {
            var busy = new ContentDialog
            {
                Title = "游戏正在运行",
                Content = "先退出游戏再删除实例。",
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            ThemeApplier.ApplyToDialog(busy);
            await busy.ShowAsync();
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除客户端实例",
            Content = $"删除实例「{_instance}」？\n\n"
                      + "「仅移除」保留 mods、存档与日志；「连数据删除」将永久删除整个实例目录，不可恢复。",
            PrimaryButtonText = "仅移除",
            SecondaryButtonText = "连数据删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        try
        {
            ClientStore.Remove(_instance, purge: result == ContentDialogResult.Secondary);
            InstanceDeleted?.Invoke(_instance);
        }
        catch (Ceobe.Core.Util.CeobeException ex)
        {
            var error = new ContentDialog
            {
                Title = "删除失败",
                Content = ex.Message,
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            ThemeApplier.ApplyToDialog(error);
            await error.ShowAsync();
        }
    }

    /// <summary>解绑事件订阅（幂等；主窗口替换工作区前会显式调用）。</summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        LaunchVm.Detach();
        _log.Detach();
    }
}
