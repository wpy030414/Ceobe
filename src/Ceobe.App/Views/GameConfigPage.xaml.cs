using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 游戏配置页 code-behind。把 <see cref="GameConfigViewModel"/> 的三级视图状态映射成 XAML 能绑定的形态：
/// <list type="bullet">
/// <item><see cref="ViewIndex"/>：RadioButtons 的三态（0 常用 / 1 分类 / 2 原始）↔ VM 的 <see cref="ConfigView"/>；</item>
/// <item><see cref="ShowGroupNav"/>：仅分类视图显示分组下拉；</item>
/// <item><see cref="ShowNoProps"/>：无 server.properties 时显示引导条。</item>
/// </list>
/// 用「无参构造 + <see cref="Load"/>」模式（与 ConsolePage/ServerPage 一致），
/// 因为本页在 XAML 里以 x:Name 声明，XAML 实例化要求无参构造。
/// </summary>
public sealed partial class GameConfigPage : UserControl, System.ComponentModel.INotifyPropertyChanged
{
    private GameConfigViewModel? _vm;

    public GameConfigPage() => InitializeComponent();

    public GameConfigViewModel Vm => _vm!;

    /// <summary>RadioButtons 选中索引 ↔ VM 的三级视图。</summary>
    public int ViewIndex
    {
        get => Vm?.View switch
        {
            ConfigView.Common => 0,
            ConfigView.Category => 1,
            ConfigView.Raw => 2,
            _ => 0,
        };
        set
        {
            if (_vm is null) return;
            _vm.View = value switch
            {
                1 => ConfigView.Category,
                2 => ConfigView.Raw,
                _ => ConfigView.Common,
            };
        }
    }

    /// <summary>分组下拉仅在「分类」视图显示。</summary>
    public Visibility ShowGroupNav =>
        Vm?.View == ConfigView.Category ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>无 server.properties 时显示引导 InfoBar（InfoBar.IsOpen 要 bool，不是 Visibility）。</summary>
    public bool ShowNoProps => Vm is { PropsExist: false };

    public void Load(GameConfigViewModel vm)
    {
        _vm = vm;
        vm.Load();
        // 视图/props 状态变化时刷新本页派生的绑定属性
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameConfigViewModel.View))
            {
                OnPropertyChanged(nameof(ViewIndex));
                OnPropertyChanged(nameof(ShowGroupNav));
            }
            else if (e.PropertyName is nameof(GameConfigViewModel.PropsExist))
            {
                OnPropertyChanged(nameof(ShowNoProps));
            }
        };
        OnPropertyChanged(nameof(ViewIndex));
        OnPropertyChanged(nameof(ShowGroupNav));
        OnPropertyChanged(nameof(ShowNoProps));
        Bindings.Update();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SaveCommand.ExecuteAsync(null);
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => _vm?.DiscardCommand.Execute(null);

    // 本页把 VM 的三级视图状态映射成派生绑定属性（ViewIndex/ShowGroupNav/ShowNoProps），
    // 这些属性运行期会变，故实现 INotifyPropertyChanged 通知 x:Bind 刷新。
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
