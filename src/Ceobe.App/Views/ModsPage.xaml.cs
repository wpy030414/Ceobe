using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// Mod 页 code-behind。上半接 <see cref="ModsViewModel"/>（Modrinth 搜索/装卸），
/// 下半接 <see cref="ImportViewModel"/>（整改项 4 一键导入）。两个 VM 各自独立可单测，
/// 本页只做绑定、手势转发与拖拽落文件。
/// </summary>
public sealed partial class ModsPage : UserControl, IReloadable, System.ComponentModel.INotifyPropertyChanged
{
    private ModsViewModel? _vm;
    private ImportViewModel? _importVm;

    public ModsPage() => InitializeComponent();

    public ModsViewModel Vm => _vm!;
    public ImportViewModel ImportVm => _importVm!;

    /// <summary>不支持模组（原版/Paper）时只显示说明。</summary>
    public Visibility ShowUnsupported =>
        _vm is { SupportsMods: false } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowContent =>
        _vm is { SupportsMods: true } ? Visibility.Visible : Visibility.Collapsed;

    public string UnsupportedText => _vm?.UnsupportedHint ?? "";

    /// <summary>识别结果一行摘要（种类 + 文件数 + 落位），供确认页展示。</summary>
    public string ImportSummary
    {
        get
        {
            if (_importVm is not { Inspected: true }) return "";
            var size = TextFormat.HumanSize(_importVm.TotalBytes);
            return $"识别为「{_importVm.KindLabel}」：{_importVm.FileCount} 个文件、{size} → {_importVm.SuggestedSubdir}"
                   + (_importVm.OverwriteCount > 0 ? $"（覆盖 {_importVm.OverwriteCount} 个）" : "");
        }
    }

    public bool NotBusy => _vm is { IsBusy: false } && _importVm is { IsBusy: false };

    /// <summary>导入被拦下时用 InfoBar 说明原因，而不是只把按钮置灰。</summary>
    public bool HasBlockedReason => _importVm?.BlockedReason is { Length: > 0 };

    public void Load(ModsViewModel vm, ImportViewModel importVm)
    {        _vm = vm;
        _importVm = importVm;
        vm.RefreshInstalled();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModsViewModel.IsBusy)) OnPropertyChanged(nameof(NotBusy));
            if (e.PropertyName is nameof(ModsViewModel.SupportsMods)) RaiseSupportChanged();
        };
        importVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ImportViewModel.IsBusy)) OnPropertyChanged(nameof(NotBusy));
            if (e.PropertyName is nameof(ImportViewModel.Inspected)
                or nameof(ImportViewModel.TotalBytes)
                or nameof(ImportViewModel.FileCount)
                or nameof(ImportViewModel.SuggestedSubdir)
                or nameof(ImportViewModel.OverwriteCount))
                OnPropertyChanged(nameof(ImportSummary));
            if (e.PropertyName is nameof(ImportViewModel.BlockedReason))
                OnPropertyChanged(nameof(HasBlockedReason));
        };
        RaiseSupportChanged();
        OnPropertyChanged(nameof(NotBusy));
        OnPropertyChanged(nameof(ImportSummary));
        OnPropertyChanged(nameof(HasBlockedReason));
        Bindings.Update();
    }

    /// <summary>
    /// 页签切回本页时刷新已安装清单：整合包页/拖拽导入都可能刚往 mods/ 写过文件，
    /// 不重读的话这里显示的还是旧列表（用户会以为导入没成功）。
    /// </summary>
    public void Reload() => _vm?.RefreshInstalled();

    private void RaiseSupportChanged()
    {
        OnPropertyChanged(nameof(ShowUnsupported));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(UnsupportedText));
    }

    private async void OnSearch(object sender, RoutedEventArgs e) =>
        await (_vm?.SearchCommand.ExecuteAsync(null) ?? Task.CompletedTask);

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _vm is not null)
        {
            e.Handled = true;
            await _vm.SearchCommand.ExecuteAsync(null);
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.InstallCommand.ExecuteAsync(null);
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.SelectedInstalled is null) return;

        // 删除 mods/ 下的文件是不可逆动作，先确认。
        var dialog = new ContentDialog
        {
            Title = "卸载 Mod",
            Content = $"卸载「{_vm.SelectedInstalled.Title}」？\n\n会从 mods/ 删除对应文件。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _vm.UninstallCommand.ExecuteAsync(null);
        }
    }

    private async void OnInspect(object sender, RoutedEventArgs e)
    {
        if (_importVm is null) return;
        // InspectAsync 接受路径参数；SourcePath 已由 TextBox 双向绑定填好
        await _importVm.InspectCommand.ExecuteAsync(_importVm.SourcePath);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_importVm is null) return;
        await _importVm.InstallCommand.ExecuteAsync(null);
        // 导入成功后 Mod 清单变了，刷新已安装列表
        _vm?.RefreshInstalled();
    }

    private void OnImportReset(object sender, RoutedEventArgs e) => _importVm?.ResetCommand.Execute(null);

    /// <summary>拖拽悬停：仅接受支持的文件类型（.jar/.mrpack/.zip），给出正确的拖放光标。</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    /// <summary>拖放落文件：取第一个受支持的文件路径，交给 ImportViewModel 识别。</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_importVm is null) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        // 只取第一个受支持的文件；拖多个时提示只处理第一个（不静默丢弃其余，见 statusText）
        var first = items.FirstOrDefault(i => ImportViewModel.IsSupportedFile(i.Path));
        if (first is null)
        {
            _importVm.StatusText = "拖入的文件不是 .jar / .mrpack / .zip";
            _importVm.HasError = true;
            return;
        }
        _importVm.SourcePath = first.Path;
        if (items.Count > 1)
        {
            _importVm.StatusText = $"拖入了 {items.Count} 个文件，本次只识别第一个：{first.Name}";
        }
        await _importVm.InspectCommand.ExecuteAsync(first.Path);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
