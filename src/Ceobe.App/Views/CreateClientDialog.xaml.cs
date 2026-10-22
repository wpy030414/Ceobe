using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 新建客户端实例对话框。
///
/// 装配（下载 400 MB）可能持续几分钟，因此主按钮点击后**不立即关闭**：
/// 先取消关闭、把进度留在原地，成功后才关。失败时对话框保持打开，
/// 用户可以改版本/加载器直接重试，不必从头再点一遍。
/// </summary>
public sealed partial class CreateClientDialog : ContentDialog
{
    private readonly ClientCreateViewModel _vm = new();

    /// <summary>创建成功的实例名（对话框关闭后主窗口据此选中）。</summary>
    public string? CreatedInstance { get; private set; }

    public ClientCreateViewModel Vm => _vm;

    public CreateClientDialog()
    {
        InitializeComponent();
        // ContentDialog 默认最大宽度 548，放不下版本 + 加载器两栏
        Resources["ContentDialogMaxWidth"] = 920.0;
        Bindings.Update();

        Opened += async (_, _) =>
        {
            ThemeApplier.ApplyToDialog(this);
            await _vm.LoadAsync();
            Bindings.Update();
            UpdatePrimaryButton();
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientCreateViewModel.CanCreate)
                or nameof(ClientCreateViewModel.IsBusy))
            {
                UpdatePrimaryButton();
            }
        };

        PrimaryButtonClick += async (_, args) =>
        {
            if (_vm.Created is not null)
            {
                CreatedInstance = _vm.Created;
                return;
            }

            // 未完成装配前不允许关闭，否则用户会以为「点了没反应」
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                await _vm.CreateCommand.ExecuteAsync(null);
                if (_vm.Created is not null)
                {
                    CreatedInstance = _vm.Created;
                    Hide();
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    private void UpdatePrimaryButton() => IsPrimaryButtonEnabled = _vm.CanCreate;

    private void OnSelectLoader(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: ClientLoaderOption option }) _vm.SelectedLoader = option;
    }
}
