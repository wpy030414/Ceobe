using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Ceobe.App.ViewModels;
using Ceobe.Core.Client;

namespace Ceobe.App.Views;

/// <summary>资源包/光影页 code-behind：开关与排序都是即时写盘，本页不缓存任何状态。</summary>
public sealed partial class ClientPacksPage : UserControl
{
    private ClientPacksViewModel? _vm;

    public ClientPacksPage() => InitializeComponent();

    public ClientPacksViewModel Vm => _vm!;

    public void Load(ClientPacksViewModel vm)
    {
        _vm = vm;
        Bindings.Update();
        vm.Reload();
    }

    private void OnTogglePack(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: ClientPackRow row }) return;
        Report(_vm.Toggle(row));
        Bindings.Update();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (_vm is null || sender is not Button { Tag: ClientPackRow row }) return;
        _vm.Move(row, delta);
        Bindings.Update();
    }

    private void OnToggleShader(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: ClientShaderRow row }) return;
        _vm.ToggleShader(row);
        Bindings.Update();
    }

    private async void OnImportPack(object sender, RoutedEventArgs e)
    {
        var path = await AskPath("导入资源包");
        if (path is null || _vm is null) return;
        Report(_vm.ImportPack(path));
        Bindings.Update();
    }

    private async void OnImportShader(object sender, RoutedEventArgs e)
    {
        var path = await AskPath("导入光影包");
        if (path is null || _vm is null) return;
        Report(_vm.ImportShader(path));
        Bindings.Update();
    }

    private void OnOpenPacksFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", ClientStore.ResourcePacksDir(_vm.DirectoryName))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    private void OnPackDragOver(object sender, DragEventArgs e) => Accept(e, "导入到 resourcepacks");

    private void OnShaderDragOver(object sender, DragEventArgs e) => Accept(e, "导入到 shaderpacks");

    private static void Accept(DragEventArgs e, string caption)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = caption;
    }

    private async void OnPackDrop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        var path = await FirstDroppedPath(e);
        if (path is null) return;
        Report(_vm.ImportPack(path));
        Bindings.Update();
    }

    private async void OnShaderDrop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        var path = await FirstDroppedPath(e);
        if (path is null) return;
        Report(_vm.ImportShader(path));
        Bindings.Update();
    }

    private static async Task<string?> FirstDroppedPath(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return null;
        var items = await e.DataView.GetStorageItemsAsync();
        return items.OfType<StorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
    }

    private async Task<string?> AskPath(string title)
    {
        var input = new TextBox { Header = "zip 的完整路径", PlaceholderText = @"D:\Downloads\pack.zip" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return input.Text.Trim();
    }

    private async void Report(string? error)
    {
        if (error is null || XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = "操作失败",
            Content = error,
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        await dialog.ShowAsync();
    }
}
