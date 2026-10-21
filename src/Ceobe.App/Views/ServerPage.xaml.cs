using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 服务器配置页 code-behind。只做绑定与手势转发；业务在 <see cref="ServerViewModel"/>。
///
/// 用「无参构造 + <see cref="Load"/>」而非构造器注入：本页在 XAML 里以 <c>x:Name</c> 声明，
/// XAML 实例化要求无参构造（带 VM 参数的构造器无法被 XAML 调用，会编译失败）。
/// 这与 <see cref="ConsolePage"/> 是同一套模式。
/// </summary>
public sealed partial class ServerPage : UserControl
{
    private ServerViewModel? _vm;

    public ServerPage() => InitializeComponent();

    /// <summary>x:Bind 绑定目标；<see cref="Load"/> 前为占位 VM。</summary>
    public ServerViewModel Vm => _vm!;

    /// <summary>实例被改名，请求主界面重建页签并刷新侧栏。</summary>
    public event Action<string>? InstanceRenamed;

    public void Load(ServerViewModel vm)
    {
        _vm = vm;
        vm.Load();
        // 改名成功后通知上层重建页签（页签标题是实例名）
        vm.Renamed += name => InstanceRenamed?.Invoke(name);
        Bindings.Update();   // x:Bind 重新求值，绑到新 VM
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SaveCommand.ExecuteAsync(null);
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => _vm?.DiscardCommand.Execute(null);
}
