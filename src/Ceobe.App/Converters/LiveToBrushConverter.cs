using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ceobe.App.Converters;

/// <summary>
/// 实例是否运行中 → 状态圆点画刷。
/// 运行中绿、停止灰——侧栏一眼能看出哪个服开着，是 Java 版 ceobe-badge 的等价物。
///
/// Color 用的是 <c>Windows.UI.Color</c>（WinRT 结构，SolidColorBrush 的构造器只认它），
/// 不是 Microsoft.UI.Color（该类型不存在）；这是探针实测确认的，不是猜的。
/// </summary>
public sealed class LiveToBrushConverter : IValueConverter
{
    /// <summary>运行中：与 Web 控制台同色系（zinc 绿 #4ADE80）。</summary>
    private static readonly SolidColorBrush Live = new(Color.FromArgb(255, 0x4A, 0xDE, 0x80));

    /// <summary>停止：中性灰，不抢注意力。</summary>
    private static readonly SolidColorBrush Idle = new(Color.FromArgb(255, 0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Live : Idle;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
