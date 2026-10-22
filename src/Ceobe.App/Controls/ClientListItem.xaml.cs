using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Controls;

public sealed partial class ClientListItem : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(ClientRow), typeof(ClientListItem),
        new PropertyMetadata(null, OnRowChanged));

    public ClientRow? Row
    {
        get => (ClientRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public ClientListItem() => InitializeComponent();

    /// <summary>
    /// 容器会被回收复用（同一个 UserControl 换绑不同的 Row），x:Bind 的 OneWay 只跟随
    /// Row 的属性变化、不会因 Row 本身换人而刷新，故这里显式重建绑定。
    /// </summary>
    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ClientListItem)d;
        control.Bindings.Update();
    }
}
