using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 控制台页 code-behind。只做绑定与手势转发；业务在 <see cref="ConsoleViewModel"/>（闸门 C）。
///
/// 2026-10-22 重构：启停/删除移到实例头（InstanceTabs），本页只负责日志与命令输入。
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
        ? "该实例由外部进程托管（CLI 启动），日志按文件增量跟随。"
        : "";

    /// <summary>日志为空时显示空状态，而不是一块看不出边界的白板。</summary>
    public bool ShowEmptyLog => Vm is null || Vm.Lines.Count == 0;

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
        vm.Lines.CollectionChanged += OnLinesChanged;

        // 日志新增时，跟随模式下自动滚到底
        OnPropertyChanged(nameof(ShowEmptyLog));
        ScrollToLatest();
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

        Unloaded += OnUnloaded;
    }

    private void OnLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowEmptyLog));
        if (_vm?.Follow == true) ScrollToLatest();
    }

    private void ScrollToLatest()
    {
        // 等 ItemsStackPanel 完成一次布局后再滚，否则 ScrollableHeight 还是旧值。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_vm is { Lines.Count: > 0 })
            {
                LogList.ScrollIntoView(_vm.Lines[^1]);
            }
        });
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await Vm.SendCommand.ExecuteAsync(null);
        InputBox.Focus(FocusState.Programmatic);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.ClearViewCommand.Execute(null);

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        if (Vm is null || Vm.Lines.Count == 0) return;

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, Vm.Lines));
        Clipboard.SetContent(package);

        Vm.HasError = false;
        Vm.StatusText = $"已复制 {Vm.Lines.Count} 行日志";
    }

    /// <summary>回车发送（对齐旧版 input.setOnAction）。</summary>
    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Vm is null) return;
        e.Handled = true;
        await Vm.SendCommand.ExecuteAsync(null);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPolling();
        if (_vm is not null) _vm.Lines.CollectionChanged -= OnLinesChanged;
        Unloaded -= OnUnloaded;
    }

    /// <summary>停止外部日志轮询。实例头启动成功后由 InstanceTabs 调用。</summary>
    public void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
