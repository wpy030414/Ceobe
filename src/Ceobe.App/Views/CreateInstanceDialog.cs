using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;

namespace Ceobe.App.Views;

/// <summary>
/// 「新建实例」对话框。接已就绪的 <see cref="CreateViewModel"/>（业务可单测），
/// 本页只做绑定与手势转发。默认值能一把过：paper / 最新版 / 2G / 自动端口 / 白名单关。
/// </summary>
public sealed class CreateInstanceDialog : ContentDialog
{
    private readonly CreateViewModel _vm;

    /// <summary>创建成功的实例名；null = 用户取消或失败。</summary>
    public string? CreatedInstance => _vm.CreatedInstance;

    public CreateInstanceDialog(InstanceManager manager)
    {
        // 默认白名单取自应用设置（整改项 2：不再硬编码 true）
        _vm = new CreateViewModel(manager, Core.Store.AppSettings.Load().DefaultWhitelistEnabled);

        Title = "新建实例";
        PrimaryButtonText = "创建";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        var name = new TextBox { Header = "实例名（小写字母/数字/_/-）", PlaceholderText = "如 my-survival" };
        name.TextChanged += (_, _) => _vm.Name = name.Text;

        var typeBox = new ComboBox { Header = "服务端类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var t in _vm.Types) typeBox.Items.Add(t.Label);
        typeBox.SelectedIndex = _vm.Types.ToList().FindIndex(t => t.Type == _vm.SelectedType);
        typeBox.SelectionChanged += (_, _) =>
        {
            if (typeBox.SelectedIndex >= 0) _vm.SelectedType = _vm.Types[typeBox.SelectedIndex].Type;
        };

        var version = new TextBox { Header = "游戏版本（留空 = 最新正式版）", PlaceholderText = "如 26.3" };
        version.TextChanged += (_, _) => _vm.GameVersion = version.Text;

        var mem = new NumberBox { Header = "内存上限 (MB)", Value = _vm.MemoryMb, Minimum = 512, Maximum = 65536, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        mem.ValueChanged += (_, e) => _vm.MemoryMb = (int)e.NewValue;

        var autoPort = new CheckBox { Content = "自动选取空闲端口", IsChecked = _vm.AutoPort };
        var port = new NumberBox { Header = "服务器端口", Value = _vm.Port, Minimum = 1, Maximum = 65535, IsEnabled = !_vm.AutoPort };
        autoPort.Checked += (_, _) => { _vm.AutoPort = true; port.IsEnabled = false; };
        autoPort.Unchecked += (_, _) => { _vm.AutoPort = false; port.IsEnabled = true; };
        port.ValueChanged += (_, e) => _vm.Port = (int)e.NewValue;

        // 整改项 2：白名单显式问一次，默认关（Java 版默认 true 又无入口，害新手朋友进不来）
        var whitelist = new CheckBox
        {
            Content = "开启白名单（只有名单内的玩家能进服）",
            IsChecked = _vm.WhitelistEnabled,
        };
        whitelist.Checked += (_, _) => _vm.WhitelistEnabled = true;
        whitelist.Unchecked += (_, _) => _vm.WhitelistEnabled = false;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CreateViewModel.StatusText) or nameof(CreateViewModel.HasError))
            {
                status.Text = _vm.StatusText;
                status.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    _vm.HasError
                        ? Windows.UI.Color.FromArgb(255, 0xE0, 0x55, 0x55)
                        : Windows.UI.Color.FromArgb(255, 0xA1, 0xA1, 0xAA));
            }
        };

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children = { name, typeBox, version, mem, autoPort, port, whitelist, status },
        };

        PrimaryButtonClick += async (_, args) =>
        {
            // 创建可能耗时（下载服务端几十秒），阻止对话框立即关闭
            args.Cancel = true;
            await _vm.CreateCommand.ExecuteAsync(null);
            if (_vm.CreatedInstance is not null) Hide();
        };
    }
}
