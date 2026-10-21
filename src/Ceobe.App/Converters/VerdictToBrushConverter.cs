using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ceobe.App.Converters;

/// <summary>
/// 穿透通道的「可达性三态」→ 文字画刷（整改项 1）。
///
/// 三态必须分三种颜色，不能合并：
/// <list type="bullet">
/// <item><c>true</c> 已实测可达 → 绿（真的通了）</item>
/// <item><c>false</c> 无法验证 → 琥珀（探测没过，但可能是防火墙/限流，不等于通道坏了）</item>
/// <item><c>null</c> 未探测 → 灰（该通道类型不做 MC 探测）</item>
/// </list>
/// 把 false 画成灰、或把 null 画成红，都会直接误导用户去修一个没坏的东西，
/// 或者以为没测过的东西已经验证过了。
/// </summary>
public sealed class VerdictToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromArgb(255, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush Warn = new(Color.FromArgb(255, 0xF5, 0xA5, 0x24));
    private static readonly SolidColorBrush Unknown = new(Color.FromArgb(255, 0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        true => Ok,
        false => Warn,
        _ => Unknown,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
