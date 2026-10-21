using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;

namespace Ceobe.App.Views;

/// <summary>
/// 控制台页 code-behind。只做绑定与手势转发；业务在 <see cref="ConsoleViewModel"/>（闸门 C）。
/// 实现 INPC 是因为 x:Bind 绑到本页的 <see cref="Vm"/>/<see cref="ShowBanner"/> 等属性，
/// 它们运行期会变（Load 之后才有 Vm）。
/// </summary>
public sealed partial class ConsolePage : UserControl, INotifyPropertyChanged
{
    private ConsoleViewModel? _vm;
    private DispatcherTimer? _pollTimer;

    public ConsolePage() => InitializeComponent();

    /// <summary>页 ViewModel；由 <see cref="Load"/> 注入。</summary>
    public ConsoleViewModel? Vm
    {
        get => _vm;
        private set { _vm = value; OnPropertyChanged(); }
    }

    /// <summary>状态条是否显示（有非空 StatusText 时显示）。</summary>
    public bool ShowBanner => !string.IsNullOrEmpty(Vm?.StatusText);

    /// <summary>状态条配色：错误红、普通信息蓝。</summary>
    public InfoBarSeverity BannerSeverity =>
        Vm?.HasError == true ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    /// <summary>外部托管提示文案。</summary>
    public string HostedHint => Vm?.IsHosted == false
        ? "该实例由外部进程托管（如 CLI 启动），日志按文件增量跟随。"
        : "";

    /// <summary>注入 ViewModel 并加载历史日志。</summary>
    public void Load(ConsoleViewModel vm)
    {
        Vm = vm;
        vm.PropertyChanged += (_, e) =>
        {
            // VM 状态变化时刷新本页派生的绑定属性
            if (e.PropertyName is nameof(ConsoleViewModel.StatusText) or nameof(ConsoleViewModel.HasError)
                or nameof(ConsoleViewModel.IsHosted))
            {
                OnPropertyChanged(nameof(ShowBanner));
                OnPropertyChanged(nameof(BannerSeverity));
                OnPropertyChanged(nameof(HostedHint));
            }
        };
        vm.LoadHistory();
        // 日志新增时，跟随模式下自动滚到底
        vm.Lines.CollectionChanged += (_, _) =>
        {
            if (vm.Follow) LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
        };
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(BannerSeverity));
        OnPropertyChanged(nameof(HostedHint));

        // 外部托管：定时按文件偏移增量跟随（本进程托管走事件流，不需要轮询）
        if (!vm.IsHosted)
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _pollTimer.Tick += (_, _) => vm.PollExternalLog();
            _pollTimer.Start();
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await Vm.StartCommand.ExecuteAsync(null);
        // 启动会切到本进程托管，事件流接管日志；停掉文件轮询
        StopPolling();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await Vm.StopCommand.ExecuteAsync(null);
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await Vm.SendCommand.ExecuteAsync(null);
        InputBox.Focus(FocusState.Programmatic);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.ClearViewCommand.Execute(null);

    /// <summary>回车发送（对齐 Java 版 input.setOnAction）。</summary>
    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Vm is null) return;
        e.Handled = true;
        await Vm.SendCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 删除确认：三选对话框（仅移除 / 连数据删除 / 取消）。
    /// 不可逆操作必须在 View 层确认，VM 只接受已决定好的 purge 模式——
    /// 这样删除逻辑能被单测覆盖，不必为测它拉起 UI。
    /// </summary>
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new ContentDialog
        {
            Title = "删除实例",
            Content = $"删除实例「{Vm.InstanceName}」？\n\n" +
                      "「仅移除」保留世界数据文件；「连数据删除」将永久删除整个实例目录，不可恢复。",
            PrimaryButtonText = "仅移除",
            SecondaryButtonText = "连数据删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        // 用 switch 语句而非表达式：表达式分支不能写 return 语句
        switch (result)
        {
            case ContentDialogResult.Primary:
                Vm.DeleteCommand.Execute(false);
                break;
            case ContentDialogResult.Secondary:
                Vm.DeleteCommand.Execute(true);
                break;
            default:
                return;   // 取消
        }
        if (Vm.Deleted)
        {
            StopPolling();
            // 通知上层关闭页签并刷新列表（View 之间不直接依赖，用附加事件冒泡）
            InstanceDeleted?.Invoke(Vm.InstanceName);
        }
    }

    /// <summary>实例已删除，请求主界面刷新侧栏（由 InstanceTabs/MainWindow 订阅）。</summary>
    public event Action<string>? InstanceDeleted;

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
