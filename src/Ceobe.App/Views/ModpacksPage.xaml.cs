using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 整合包页 code-behind。业务全在 <see cref="ModpacksViewModel"/>；
/// 本页只把「新建成功」这件事冒泡给主窗口（新实例需要出现在侧栏并被选中）。
///
/// 与 ConsolePage/ServerPage 一致的「无参构造 + Load」模式：XAML 以 x:Name 声明本页，
/// 实例化要求无参构造，VM 由 <see cref="InstanceTabs"/> 注入。
/// </summary>
public sealed partial class ModpacksPage : UserControl, IReloadable, System.ComponentModel.INotifyPropertyChanged
{
    private ModpacksViewModel? _vm;

    public ModpacksPage() => InitializeComponent();

    public ModpacksViewModel Vm => _vm!;

    /// <summary>新建实例成功（主窗口据此刷新侧栏并选中新实例）。</summary>
    public event Action<string>? InstanceCreated;

    /// <summary>两个动作按钮共用的忙碌态。</summary>
    public bool NotBusy => _vm is { IsBusy: false };

    /// <summary>本实例是否装不了整合包（原版/Paper）。</summary>
    public bool ShowUnsupported => _vm is { SupportsModpacks: false };

    /// <summary>进度未知（-1）时显示不定进度条。</summary>
    public bool ProgressIndeterminate => _vm is { IsBusy: true, Progress: < 0 };

    /// <summary>搜索完成且没有结果时显示空状态。</summary>
    public bool ShowEmptyResults => _vm is { IsBusy: false, Results.Count: 0 };

    /// <summary>
    /// NumberBox.Value 是 double，而 VM 的 NewMemoryMb 是 int。
    /// 直接绑 int 会撞 WMC1121（类型不匹配），故在视图层做一层 double 投影——
    /// 与 GameConfigPage 里 PropField.NumberValueDouble 是同一个原因。
    /// </summary>
    public double NewMemoryMbDouble
    {
        get => _vm?.NewMemoryMb ?? 4096;
        set
        {
            if (_vm is not null) _vm.NewMemoryMb = (int)Math.Round(value);
        }
    }

    public void Load(ModpacksViewModel vm)
    {
        _vm = vm;
        // 运行态决定「装到本实例」是否可用，页签每次显示都要重新读
        vm.Refresh();
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ModpacksViewModel.IsBusy):
                    OnPropertyChanged(nameof(NotBusy));
                    OnPropertyChanged(nameof(ProgressIndeterminate));
                    OnPropertyChanged(nameof(ShowEmptyResults));
                    break;
                case nameof(ModpacksViewModel.Progress):
                    OnPropertyChanged(nameof(ProgressIndeterminate));
                    break;
            }
        };
        vm.Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyResults));
        RaiseStatic();
        Bindings.Update();
    }

    /// <summary>页签切回本页时刷新运行态（Stop/Start 会改变 CanInstallHere）。</summary>
    public void Reload() => _vm?.Refresh();

    private void RaiseStatic()
    {
        OnPropertyChanged(nameof(NotBusy));
        OnPropertyChanged(nameof(ShowUnsupported));
        OnPropertyChanged(nameof(ProgressIndeterminate));
        OnPropertyChanged(nameof(ShowEmptyResults));
        OnPropertyChanged(nameof(NewMemoryMbDouble));
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SearchCommand.ExecuteAsync(null);
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _vm is not null)
        {
            e.Handled = true;
            await _vm.SearchCommand.ExecuteAsync(null);
        }
    }

    private async void OnInstallHere(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.InstallHereCommand.ExecuteAsync(null);
    }

    private async void OnCreateAndInstall(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.CreateAndInstallCommand.ExecuteAsync(null);
        // 只有真正建成功才会被赋值；失败时为 null，不冒泡（避免侧栏刷新到不存在的实例）
        if (_vm.CreatedInstance is { Length: > 0 } created) InstanceCreated?.Invoke(created);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
