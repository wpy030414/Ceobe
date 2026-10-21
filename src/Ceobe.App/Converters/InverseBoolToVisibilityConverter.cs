using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Ceobe.App.Converters;

/// <summary>bool 为 true 时隐藏，false 时显示。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not Visibility.Visible;
}
