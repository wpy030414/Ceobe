using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>设置页 code-behind：账户切换、离线账户新增、实例参数保存。</summary>
public sealed partial class ClientSettingsPage : UserControl
{
    private ClientSettingsViewModel? _vm;

    public ClientSettingsPage() => InitializeComponent();

    public ClientSettingsViewModel Vm => _vm!;

    public void Load(ClientSettingsViewModel vm)
    {
        _vm = vm;
        Bindings.Update();
        vm.Reload();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientSettingsViewModel.Accounts)
                or nameof(ClientSettingsViewModel.Javas))
            {
                Bindings.Update();
            }
        };
    }

    private void OnUseAccount(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: ClientAccountRow row }) return;
        _vm.UseAccount(row);
        Bindings.Update();
    }

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        _vm?.AddOfflineCommand.Execute(null);
        Bindings.Update();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm?.SaveCommand.Execute(null);
        Bindings.Update();
    }
}
