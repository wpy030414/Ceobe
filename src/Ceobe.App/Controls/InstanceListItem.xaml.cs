using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Controls;

/// <summary>
/// NavigationView 侧栏的一行实例：名称 + 状态点 + 类型/版本/端口 · 状态文字。
/// <see cref="Row"/> 是依赖属性，容器复用时在回调里重新求值全部 x:Bind。
/// </summary>
public sealed partial class InstanceListItem : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(InstanceRow), typeof(InstanceListItem),
        new PropertyMetadata(null, OnRowChanged));

    public InstanceRow? Row
    {
        get => (InstanceRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public InstanceListItem()
    {
        InitializeComponent();
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((InstanceListItem)d).Bindings.Update();
}
