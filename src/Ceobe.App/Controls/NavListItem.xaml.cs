using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Controls;

/// <summary>
/// NavigationView 侧栏的一行：名称 + 状态点 + 次行说明。
/// 服务端行与客户端行共用这一个控件，行契约见 <see cref="INavRow"/>。
///
/// <see cref="Row"/> 是依赖属性：容器复用时在回调里重新求值全部 x:Bind——
/// x:Bind 的 OneWay 只跟随 Row 的属性变化，不会因 Row 本身换人而刷新。
/// </summary>
public sealed partial class NavListItem : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(INavRow), typeof(NavListItem),
        new PropertyMetadata(null, OnRowChanged));

    public INavRow? Row
    {
        get => (INavRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public NavListItem()
    {
        InitializeComponent();
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((NavListItem)d).Bindings.Update();
}
