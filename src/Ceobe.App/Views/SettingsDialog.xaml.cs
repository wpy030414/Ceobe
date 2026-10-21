using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;

namespace Ceobe.App.Views;

/// <summary>
/// 设置对话框：主题三态 + 默认白名单 + 数据目录搬家 + 关于。
/// 业务全在 <see cref="SettingsViewModel"/>（闸门 C），本页只做绑定与手势转发。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog, INotifyPropertyChanged
{
    private readonly string _initialHome;

    /// <summary>搬家是否真正改变了数据目录；主窗口据此重建工作区。</summary>
    public bool HomeMoved { get; private set; }

    public SettingsViewModel Vm { get; }

    /// <summary>主按钮文案对应的版本号（CalVer）。</summary>
    public string AppDisplayVersion
    {
        get
        {
            var informational = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = informational ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
            return "版本 " + version.Split('+')[0];
        }
    }

    public bool HasOneDriveWarning => Vm.OneDriveWarning is { Length: > 0 };

    public bool NotMoving => !Vm.IsMoving;

    /// <summary>搬家预演的一句话摘要。</summary>
    public string PlanSummary => Vm.Plan is { } plan
        ? $"将搬 {plan.InstanceCount} 个实例、{TextFormat.HumanSize(plan.TotalBytes)} → {plan.Target}"
        : "";

    /// <summary>非错误状态显示普通状态文字；错误交给 InfoBar，避免重复。</summary>
    public bool ShowPlainStatus => !Vm.HasError && Vm.StatusText.Length > 0;

    public SettingsDialog(InstanceManager manager)
    {
        Vm = new SettingsViewModel(manager);
        _initialHome = Vm.CurrentHome;

        InitializeComponent();
        Vm.PropertyChanged += OnVmPropertyChanged;
        Bindings.Update();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.Plan):
                OnPropertyChanged(nameof(PlanSummary));
                break;
            case nameof(SettingsViewModel.IsMoving):
                OnPropertyChanged(nameof(NotMoving));
                break;
            case nameof(SettingsViewModel.OneDriveWarning):
                OnPropertyChanged(nameof(HasOneDriveWarning));
                break;
            case nameof(SettingsViewModel.StatusText):
            case nameof(SettingsViewModel.HasError):
                OnPropertyChanged(nameof(ShowPlainStatus));
                break;
            case nameof(SettingsViewModel.CurrentHome):
                // 搬家成功后 CurrentHome 变化，对话框里的只读框跟着更新
                break;
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 即时预览：改完立刻能看出效果，不必先保存再关窗。
        // ContentDialog 位于 PopupRoot，不继承根元素主题，所以自身也要设 RequestedTheme。
        if (XamlRoot?.Content is FrameworkElement root)
        {
            ThemeApplier.Apply(root, Vm.Theme);
            RequestedTheme = ThemeApplier.Current;
        }
    }

    private async void OnPlanMove(object sender, RoutedEventArgs e) =>
        await Vm.PlanMoveCommand.ExecuteAsync(null);

    private async void OnExecuteMove(object sender, RoutedEventArgs e)
    {
        var before = Vm.CurrentHome;
        await Vm.ExecuteMoveCommand.ExecuteAsync(null);
        if (!string.Equals(before, Vm.CurrentHome, StringComparison.OrdinalIgnoreCase))
        {
            HomeMoved = true;
        }
    }

    private void OnCancelMove(object sender, RoutedEventArgs e) => Vm.CancelMoveCommand.Execute(null);

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        Vm.SaveSettingsCommand.Execute(null);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
