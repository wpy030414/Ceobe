using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Controls;

/// <summary>
/// 按 PropField 的类型选择输入控件模板（Bool / Int / Enum / String）。
///
/// 为什么不能用「四个控件叠在一起 + Visibility」：被折叠的控件仍会参与 x:Bind 初始化，
/// NumberBox 会把 String 字段写成 "0"、隐藏的 TextBox 会把 Bool 字段写成空串——
/// 实测直接把 motd 从 "A Minecraft Server" 改成 "0"，并制造出假校验错误。
/// 只实例化与类型匹配的那一个控件，才是正确做法。
/// </summary>
public sealed class PropInputTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BoolTemplate { get; set; }
    public DataTemplate? IntTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }
    public DataTemplate? StringTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        Select(item, null);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        Select(item, container);

    private DataTemplate? Select(object item, DependencyObject? container)
    {
        if (item is not PropField field) return StringTemplate;
        if (field.IsBool) return BoolTemplate;
        if (field.IsInt) return IntTemplate;
        if (field.IsEnum) return EnumTemplate;
        return StringTemplate;
    }
}
