using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;

namespace Ceobe.App.Views;

/// <summary>
/// 「新建实例」单页表单。业务全在 <see cref="CreateViewModel"/>（可单测），
/// 本页只做绑定与手势转发。
/// </summary>
public sealed partial class CreateInstanceDialog : ContentDialog
{
    /// <summary>创建成功的实例名；null = 用户取消或失败。</summary>
    public string? CreatedInstance => Vm.CreatedInstance;

    public CreateViewModel Vm { get; }

    private bool _opened;

    public CreateInstanceDialog(InstanceManager manager)
    {
        Vm = new CreateViewModel(manager, AppSettings.Load().DefaultWhitelistEnabled);
        InitializeComponent();
        Bindings.Update();
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _opened = true;
        NameBox.Focus(FocusState.Programmatic);
        await Vm.LoadVersionsAsync();
    }

    private async void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化阶段 SelectedItem 也会变化；只在对话框真正打开后加载版本。
        if (!_opened) return;
        if (TypeBox.SelectedItem is TypeOption option) Vm.SelectedTypeOption = option;
        await Vm.LoadVersionsAsync();
    }

    private async void OnReloadVersions(object sender, RoutedEventArgs e) =>
        await Vm.LoadVersionsAsync();

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 创建可能耗时（下载服务端几十秒），阻止对话框立即关闭
        args.Cancel = true;
        await Vm.CreateCommand.ExecuteAsync(null);
        if (Vm.CreatedInstance is not null) Hide();
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 创建进行中不允许关窗，避免留下半成品实例
        args.Cancel = Vm.IsBusy;
    }
}
