using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 玩家页 code-behind。业务全在 <see cref="PlayerListViewModel"/>（可单测）；
/// 本页只负责：分段选择 → 四张表的显隐、按钮手势 → 命令、行内按钮的 Tag 取名字。
///
/// 为什么不把「移除」写成 Command 绑定：DataTemplate 里绑到 UserControl 的命令需要
/// ElementName + 经典 Binding，而 Vm 是 Load 之后才注入的，绑定求值时机很脆。
/// Tag 传名字 + Click 处理是 WinUI 里更稳的写法，代价只是多几行 code-behind。
/// </summary>
public sealed partial class PlayersPage : UserControl, IReloadable, System.ComponentModel.INotifyPropertyChanged
{
    private PlayerListViewModel? _vm;

    public PlayersPage() => InitializeComponent();

    public PlayerListViewModel Vm => _vm!;

    /// <summary>当前分段（0 白名单 / 1 OP / 2 封禁玩家 / 3 封禁 IP）。</summary>
    public int SectionIndex
    {
        get => _section;
        set
        {
            if (_section == value) return;
            _section = value;
            RaiseSections();
        }
    }

    private int _section;

    public bool WlVisible => _section == 0;
    public bool OpVisible => _section == 1;
    public bool BanVisible => _section == 2;
    public bool IpBanVisible => _section == 3;

    /// <summary>ComboBox 索引 ↔ OP 等级 1..4（默认 4，故索引 3）。</summary>
    public int OpLevelIndex
    {
        get => Math.Clamp((_vm?.NewOpLevel ?? 4) - 1, 0, 3);
        set
        {
            if (_vm is not null) _vm.NewOpLevel = Math.Clamp(value, 0, 3) + 1;
        }
    }

    public void Load(PlayerListViewModel vm)
    {
        _vm = vm;
        vm.Load();
        RaiseSections();
        OnPropertyChanged(nameof(OpLevelIndex));
        Bindings.Update();
    }

    /// <summary>页签切回本页时重读磁盘（服务端可能在这期间改过名单文件）。</summary>
    public void Reload() => _vm?.Load();

    private void RaiseSections()
    {
        OnPropertyChanged(nameof(WlVisible));
        OnPropertyChanged(nameof(OpVisible));
        OnPropertyChanged(nameof(BanVisible));
        OnPropertyChanged(nameof(IpBanVisible));
    }

    // ===== 白名单 =====

    private void OnToggleWhitelist(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not CheckBox cb) return;
        // Click 时 IsChecked 已翻转；显式回写一次，避免绑定时序差异导致读到旧值
        _vm.WhitelistEnabled = cb.IsChecked == true;
        _vm.ToggleWhitelistCommand.Execute(null);
    }

    private void OnToggleEnforce(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not CheckBox cb) return;
        _vm.EnforceWhitelist = cb.IsChecked == true;
        _vm.ToggleEnforceWhitelistCommand.Execute(null);
    }

    private async void OnAddWhitelist(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.AddToWhitelistCommand.ExecuteAsync(null);
    }

    private void OnRemoveWhitelist(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as FrameworkElement)?.Tag is string name)
            _vm.RemoveFromWhitelistCommand.Execute(name);
    }

    private async void OnImportWhitelist(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ImportWhitelistCommand.ExecuteAsync(BulkBox.Text);
    }

    private async void OnImportFromOps(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ImportFromOpsCommand.ExecuteAsync(null);
    }

    // ===== OP =====

    private async void OnAddOp(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.AddOpCommand.ExecuteAsync(null);
    }

    private void OnRemoveOp(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as FrameworkElement)?.Tag is string name)
            _vm.RemoveOpCommand.Execute(name);
    }

    // ===== 封禁 =====

    private async void OnBanPlayer(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.BanPlayerCommand.ExecuteAsync(null);
    }

    private void OnPardonPlayer(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as FrameworkElement)?.Tag is string name)
            _vm.PardonPlayerCommand.Execute(name);
    }

    private void OnBanIp(object sender, RoutedEventArgs e) => _vm?.BanIpCommand.Execute(null);

    private void OnPardonIp(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as FrameworkElement)?.Tag is string ip)
            _vm.PardonIpCommand.Execute(ip);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
