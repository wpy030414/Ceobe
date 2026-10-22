using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 服务器页 code-behind：探测与加入。加入的落地在 ClientTabs（切回启动页并启动），
/// 本页只把「用户点了哪台」翻译成地址。
/// </summary>
public sealed partial class ClientServersPage : UserControl
{
    private ClientServersViewModel? _vm;

    public ClientServersPage() => InitializeComponent();

    public ClientServersViewModel Vm => _vm!;

    public bool NotPinging => _vm is { IsPinging: false };

    public void Load(ClientServersViewModel vm)
    {
        _vm = vm;
        Bindings.Update();
        vm.Reload();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientServersViewModel.IsPinging)) Bindings.Update();
            if (e.PropertyName is nameof(ClientServersViewModel.Servers)) Bindings.Update();
        };
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        Bindings.Update();
        await _vm.PingAllAsync();
        Bindings.Update();
    }

    private void OnSync(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SyncServersDat();
        Bindings.Update();
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: ClientServerRow row }) return;
        if (!row.VersionMatches) return;
        _vm.Join(row);
        await Task.CompletedTask;
    }
}
