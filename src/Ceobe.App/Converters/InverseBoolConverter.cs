using Microsoft.UI.Xaml.Data;

namespace Ceobe.App.Converters;

/// <summary>bool 取反。用于「自动端口 → 端口框禁用」这类绑定。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not true;
}
