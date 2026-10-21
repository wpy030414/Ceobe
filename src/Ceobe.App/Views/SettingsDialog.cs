using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Runtime;

namespace Ceobe.App.Views;

/// <summary>
/// 设置对话框（整改项 3：数据目录可改 + 搬家向导）。接已就绪的 <see cref="SettingsViewModel"/>。
/// 纯 C# 构建 UI（不新增 XAML 文件），缩小非打包 WinUI 3 首次启动验证的编译风险面；
/// 业务逻辑全在 VM，已由 SettingsViewModelTest 覆盖。
/// </summary>
public sealed class SettingsDialog : ContentDialog
{
    private readonly SettingsViewModel _vm;
    private readonly TextBox _homeBox;
    private readonly TextBlock _warning;
    private readonly TextBlock _status;
    private readonly StackPanel _planPanel;

    public SettingsDialog(InstanceManager manager)
    {
        _vm = new SettingsViewModel(manager);

        Title = "设置";
        PrimaryButtonText = "保存";
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Close;

        _homeBox = new TextBox { Text = _vm.CurrentHome, IsReadOnly = true, Header = "当前数据目录" };
        _warning = new TextBlock
        {
            Text = _vm.OneDriveWarning ?? "",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC0, 0x39, 0x2B)),
            Visibility = _vm.OneDriveWarning is null ? Visibility.Collapsed : Visibility.Visible,
        };
        _status = new TextBlock { Text = _vm.StatusText, TextWrapping = TextWrapping.Wrap };
        _planPanel = new StackPanel { Spacing = 6 };

        var newHome = new TextBox { Header = "新数据目录", PlaceholderText = "如 D:\\ceobe" };
        var planBtn = new Button { Content = "预演搬家（只读，不改文件）" };
        var doBtn = new Button { Content = "执行搬家" };
        var cancelBtn = new Button { Content = "放弃搬家" };

        // 目录选择器（FolderPicker）在非打包 WinUI 3 下需要真实 Window 句柄做
        // InitializeWithWindow，ContentDialog 上拿不到——故先用文本框手填路径，
        // 浏览按钮待主窗口把 Window 传下来后再接（见 MainWindow）。搬家本身不依赖选择器。
        newHome.TextChanged += (_, _) => _vm.ProposedHome = newHome.Text;

        planBtn.Click += async (_, _) =>
        {
            await _vm.PlanMoveCommand.ExecuteAsync(null);
            RenderPlan();
        };
        doBtn.Click += async (_, _) =>
        {
            await _vm.ExecuteMoveCommand.ExecuteAsync(null);
            RenderPlan();
            _homeBox.Text = _vm.CurrentHome;
        };
        cancelBtn.Click += (_, _) =>
        {
            _vm.CancelMoveCommand.Execute(null);
            RenderPlan();
        };

        var themeToggle = new ToggleSwitch
        {
            Header = "深色主题",
            IsOn = _vm.IsDark(),
        };
        themeToggle.Toggled += (_, _) => _vm.Theme = themeToggle.IsOn ? "dark" : "light";

        var whitelistToggle = new ToggleSwitch
        {
            Header = "新建实例默认开启白名单",
            IsOn = _vm.DefaultWhitelistEnabled,
        };
        whitelistToggle.Toggled += (_, _) => _vm.DefaultWhitelistEnabled = whitelistToggle.IsOn;

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 460,
            Children =
            {
                _homeBox, _warning,
                new Border { Height = 1, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x40, 0x40, 0x40)) },
                newHome,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { planBtn, doBtn, cancelBtn } },
                _planPanel,
                new Border { Height = 1, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x40, 0x40, 0x40)) },
                themeToggle, whitelistToggle,
                _status,
            },
        };

        PrimaryButtonClick += (_, _) =>
        {
            _vm.Theme = themeToggle.IsOn ? "dark" : "light";
            _vm.DefaultWhitelistEnabled = whitelistToggle.IsOn;
            _vm.SaveSettingsCommand.Execute(null);
        };
    }

    /// <summary>渲染预演结果：阻塞项红、警告黄、清单与体量。</summary>
    private void RenderPlan()
    {
        _status.Text = _vm.StatusText;
        _status.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            _vm.HasError
                ? Windows.UI.Color.FromArgb(255, 0xE0, 0x55, 0x55)
                : Windows.UI.Color.FromArgb(255, 0xA1, 0xA1, 0xAA));
        _planPanel.Children.Clear();
        var plan = _vm.Plan;
        if (plan is null) return;

        foreach (var b in plan.Blockers)
        {
            _planPanel.Children.Add(new TextBlock
            {
                Text = "✕ " + b, TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x55, 0x55)),
            });
        }
        foreach (var w in plan.Warnings)
        {
            _planPanel.Children.Add(new TextBlock
            {
                Text = "⚠ " + w, TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xA0, 0x40)),
            });
        }
        _planPanel.Children.Add(new TextBlock
        {
            Text = $"将搬 {plan.InstanceCount} 个实例、{ImportViewModel.HumanSize(plan.TotalBytes)}" +
                   $" → {plan.Target}",
            TextWrapping = TextWrapping.Wrap,
        });
    }
}

/// <summary>SettingsViewModel 的主题便捷判断（避免 View 里直接比对字符串）。</summary>
internal static class SettingsVmThemeExt
{
    public static bool IsDark(this SettingsViewModel vm) =>
        string.Equals(vm.Theme, "dark", System.StringComparison.OrdinalIgnoreCase);
}
