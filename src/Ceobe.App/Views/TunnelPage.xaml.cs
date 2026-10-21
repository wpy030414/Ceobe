using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Ceobe.App.ViewModels;
using Ceobe.Core.Tunnel;

namespace Ceobe.App.Views;

/// <summary>
/// 穿透页 code-behind。业务在 <see cref="TunnelViewModel"/>；
/// 本页只做三件事：复制地址到剪贴板、把按钮手势转成命令、把绑定冲突暴露成 InfoBar 的开关。
/// </summary>
public sealed partial class TunnelPage : UserControl, IReloadable, System.ComponentModel.INotifyPropertyChanged
{
    private TunnelViewModel? _vm;

    public TunnelPage() => InitializeComponent();

    public TunnelViewModel Vm => _vm!;

    /// <summary>InfoBar.IsOpen 要 bool；VM 里 BindingWarning 是可空字符串，故在此做一层投影。</summary>
    public bool HasBindingWarning => _vm?.BindingWarning is { Length: > 0 };

    public void Load(TunnelViewModel vm)
    {
        _vm = vm;
        vm.Refresh();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TunnelViewModel.BindingWarning))
                OnPropertyChanged(nameof(HasBindingWarning));
        };
        OnPropertyChanged(nameof(HasBindingWarning));
        Bindings.Update();
    }

    /// <summary>页签切回本页时重查通道状态（通道可能在别处被关掉，或地址已轮换）。</summary>
    public void Reload() => _vm?.Refresh();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string addr || addr.Length == 0) return;
        var package = new DataPackage();
        package.SetText(addr);
        Clipboard.SetContent(package);
        if (_vm is not null) _vm.StatusText = $"已复制 {addr}";
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if ((sender as FrameworkElement)?.Tag is not TunnelKind kind) return;
        await _vm.ToggleCommand.ExecuteAsync(kind.Id());
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
