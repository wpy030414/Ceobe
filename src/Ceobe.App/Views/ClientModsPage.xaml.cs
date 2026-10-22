using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// Mod 页 code-behind：搜索/筛选、启停、删除、导入。
/// 导入走「拖放 + 手填路径」——非打包 WinUI 的 FileOpenPicker 需要 HWND，
/// 在 UserControl 里拿不到（AGENTS 的已知易错点），拖放是更可靠的主路径。
/// </summary>
public sealed partial class ClientModsPage : UserControl
{
    private ClientModsViewModel? _vm;

    public ClientModsPage() => InitializeComponent();

    public ClientModsViewModel Vm => _vm!;

    public string CountsText => _vm is null
        ? ""
        : $"{_vm.AllCount} 个 Mod · {_vm.MissingCount} 个缺前置 · {_vm.DisabledCount} 个已停用";

    public void Load(ClientModsViewModel vm)
    {
        _vm = vm;
        Bindings.Update();
        vm.Reload();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientModsViewModel.AllCount)
                or nameof(ClientModsViewModel.MissingCount)
                or nameof(ClientModsViewModel.DisabledCount))
            {
                Bindings.Update();
            }
        };
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is ComboBox { SelectedItem: ComboBoxItem item })
        {
            _vm.Filter = item.Content?.ToString() ?? "全部";
        }
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: ClientModRow row }) return;
        var error = _vm.Toggle(row);
        if (error is not null) await ShowError(error);
        Bindings.Update();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var row = _vm.Selected;
        if (row is null) return;

        var dialog = new ContentDialog
        {
            Title = "删除 Mod 文件",
            Content = $"删除 {row.FileName}？\n\n文件会被永久删除，无法通过「启用」恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var error = _vm.RemoveMod(row);
        if (error is not null) await ShowError(error);
        Bindings.Update();
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var input = new TextBox
        {
            PlaceholderText = @"例如 D:\Downloads\sodium-fabric-0.5.11.jar",
            Header = "jar 的完整路径",
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            Title = "导入 Mod",
            Content = input,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var error = _vm.Import(input.Text.Trim());
        if (error is not null) await ShowError(error);
        Bindings.Update();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "导入到 mods";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        string? lastError = null;
        foreach (var item in items.OfType<StorageFile>())
        {
            if (string.IsNullOrEmpty(item.Path)) continue;
            lastError = _vm.Import(item.Path) ?? lastError;
        }
        if (lastError is not null) await ShowError(lastError);
        Bindings.Update();
    }

    private async Task ShowError(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "操作失败",
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        await dialog.ShowAsync();
    }
}
