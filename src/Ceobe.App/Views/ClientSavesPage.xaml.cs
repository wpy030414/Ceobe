using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Client;

namespace Ceobe.App.Views;

/// <summary>存档页 code-behind。备份前必须确认游戏已退出（半写的 region 文件备份出来不可用）。</summary>
public sealed partial class ClientSavesPage : UserControl
{
    private ClientSavesViewModel? _vm;

    public ClientSavesPage() => InitializeComponent();

    public ClientSavesViewModel Vm => _vm!;

    public void Load(ClientSavesViewModel vm)
    {
        _vm = vm;
        Bindings.Update();
        vm.Reload();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientSavesViewModel.HasError)
                or nameof(ClientSavesViewModel.StatusMessage))
            {
                Bindings.Update();
            }
        };
    }

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: ClientSaveCard row }) return;
        _vm.Backup(row);
        Bindings.Update();
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var input = new TextBox
        {
            Header = "备份 zip 的完整路径",
            PlaceholderText = @"D:\Ceobe\clients\生存重启\backups\世界-20261022-120000.zip",
        };
        var dialog = new ContentDialog
        {
            Title = "还原存档",
            Content = input,
            PrimaryButtonText = "还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        ThemeApplier.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.Restore(input.Text.Trim());
        Bindings.Update();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", ClientStore.SavesDir(_vm.InstanceName))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }
}
