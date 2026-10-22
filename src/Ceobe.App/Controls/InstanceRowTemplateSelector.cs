using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Controls;

/// <summary>
/// 侧栏行模板选择器：服务端与客户端是两种行类型，共用一个 NavigationView。
///
/// 为什么用 Selector 而不是在模式切换时替换 <c>NavigationView.MenuItemTemplate</c>：
/// 模板定义在 App.xaml 资源里（Window 没有 Resources，DataTemplate 只能挂在那儿），
/// 用 Selector 可以让两套模板都留在 XAML 里，C# 侧只做一次赋值，切换模式时不必碰模板对象。
/// </summary>
public sealed class InstanceRowTemplateSelector : DataTemplateSelector
{
    protected override DataTemplate? SelectTemplateCore(object item) => Select(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => Select(item);

    private static DataTemplate? Select(object item) => item switch
    {
        ClientRow => Application.Current.Resources.TryGetValue("ClientRowTemplate", out var c)
            ? c as DataTemplate
            : null,
        InstanceRow => Application.Current.Resources.TryGetValue("ServerRowTemplate", out var s)
            ? s as DataTemplate
            : null,
        _ => null,
    };
}
