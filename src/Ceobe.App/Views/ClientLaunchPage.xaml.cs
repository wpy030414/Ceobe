using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>启动页 code-behind：只转发手势到 VM，业务全在 <see cref="ClientLaunchViewModel"/>。</summary>
public sealed partial class ClientLaunchPage : UserControl
{
    private ClientLaunchViewModel? _vm;

    public ClientLaunchPage() => InitializeComponent();

    public ClientLaunchViewModel Vm => _vm!;

    public void Load(ClientLaunchViewModel vm)
    {
        _vm = vm;
        // x:Bind 在 InitializeComponent 时对 null 的 Vm 求值过一次，赋值后必须重算
        Bindings.Update();
        vm.Reload();
    }

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.IsLive) return;
        await _vm.LaunchCommand.ExecuteAsync(null);
    }

    private async void OnRepair(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.RepairCommand.ExecuteAsync(null);
    }

    private void OnMemoryChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SaveMemoryCommand.Execute(null);
    }
}
