using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Ceobe.App.Converters;

/// <summary>
/// bool → Visibility。用于游戏配置页按 <see cref="ViewModels.PropField"/> 的类型
/// 显示对应输入控件（Bool/Int/Enum/String 各一个，互斥显示）。
///
/// VM 层的类型判断刻意返回纯 bool（不引用 WinUI 类型，保持可无窗口单测），
/// 到 View 层才经这个转换器映射成 Visibility——这样 UI 依赖被隔离在 View 一侧。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
