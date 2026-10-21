using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;

namespace Ceobe.App.Views;

/// <summary>
/// 设置对话框：主题三态 + 默认白名单 + 数据目录 + 关于。
/// 数据目录用文件管理器选择，点「保存」后如果目录变了会自动搬家。
/// 业务全在 <see cref="SettingsViewModel"/>（闸门 C），本页只做绑定与手势转发。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog, INotifyPropertyChanged
{
    /// <summary>搬家是否真正改变了数据目录；主窗口据此重建工作区。</summary>
    public bool HomeMoved { get; private set; }

    public SettingsViewModel Vm { get; }

    /// <summary>CalVer 展示形态：26.10.22 → 26.10.22；26.10.22.01 → 26.10.22-01（XAML 里加「版本：」标签）。</summary>
    public string AppDisplayVersion
    {
        get
        {
            var informational = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = (informational ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown")
                .Split('+')[0];
            var parts = version.Split('.');
            return parts.Length >= 4
                ? string.Join(".", parts[..^1]) + "-" + parts[^1]
                : version;
        }
    }

    public bool HasOneDriveWarning => Vm.OneDriveWarning is { Length: > 0 };

    /// <summary>非错误状态显示普通状态文字；错误交给 InfoBar，避免重复。</summary>
    public bool ShowPlainStatus => !Vm.HasError && Vm.StatusText.Length > 0;

    public SettingsDialog(InstanceManager manager)
    {
        Vm = new SettingsViewModel(manager);

        InitializeComponent();
        Vm.PropertyChanged += OnVmPropertyChanged;
        Bindings.Update();
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // SettingsExpander/Expander 在部分主题下会默认展开；打开动画期间的焦点搜索也可能把它拉开。
        // 先立即收一次，再等一帧收第二次，保证默认是折叠的。
        AboutExpander.IsExpanded = false;
        await Task.Delay(150);
        AboutExpander.IsExpanded = false;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.OneDriveWarning):
                OnPropertyChanged(nameof(HasOneDriveWarning));
                break;
            case nameof(SettingsViewModel.StatusText):
            case nameof(SettingsViewModel.HasError):
                OnPropertyChanged(nameof(ShowPlainStatus));
                break;
        }
    }

    private async void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null) return;

        try
        {
            var picker = new FolderPicker(MainWindow.Instance.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is not null && !string.IsNullOrWhiteSpace(result.Path))
            {
                Vm.ProposedHome = result.Path;
            }
        }
        catch (Exception ex)
        {
            Vm.HasError = true;
            Vm.StatusText = "打开文件夹选择器失败: " + ex.Message;
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

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 保存可能需要搬家（耗时），先阻止关闭；成功后再 Hide()。
        args.Cancel = true;
        var before = Vm.CurrentHome;
        var ok = await Vm.SaveAllAsync();
        HomeMoved = !string.Equals(before, Vm.CurrentHome, StringComparison.OrdinalIgnoreCase);
        if (ok) Hide();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
