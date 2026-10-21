using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Controls;

/// <summary>
/// 单行配置项：中文名/键名/生效方式 + 类型化输入控件 + 校验错误。
/// 只实例化与字段类型匹配的那一个控件（见 <see cref="PropInputTemplateSelector"/>）。
/// </summary>
public sealed partial class PropFieldRow : UserControl
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(
        nameof(Field), typeof(PropField), typeof(PropFieldRow),
        new PropertyMetadata(null, OnFieldChanged));

    public PropField? Field
    {
        get => (PropField?)GetValue(FieldProperty);
        set => SetValue(FieldProperty, value);
    }

    public PropFieldRow()
    {
        InitializeComponent();
    }

    private static void OnFieldChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PropFieldRow)d).Bindings.Update();

    /// <summary>
    /// ComboBox 的 SelectedValue 可能先于 ItemsSource 求值；Loaded 时按当前值再选一次。
    /// </summary>
    private void OnEnumLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not PropField field) return;
        var match = field.Options.FirstOrDefault(o => o == field.Value);
        if (match is not null) combo.SelectedItem = match;
    }
}
