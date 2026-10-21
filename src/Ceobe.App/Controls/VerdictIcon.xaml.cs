using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ceobe.App.Controls;

/// <summary>
/// 穿透通道可达性三态图标：true = 已实测可达，false = 无法验证，null = 未探测。
/// 三态必须视觉可分（与 TunnelViewModel 的语义一致），但文字结论永远同时显示。
/// </summary>
public sealed partial class VerdictIcon : UserControl
{
    public static readonly DependencyProperty VerdictProperty = DependencyProperty.Register(
        nameof(Verdict), typeof(bool?), typeof(VerdictIcon),
        new PropertyMetadata(null, OnVerdictChanged));

    public bool? Verdict
    {
        get => (bool?)GetValue(VerdictProperty);
        set => SetValue(VerdictProperty, value);
    }

    public VerdictIcon()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    private static void OnVerdictChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((VerdictIcon)d).UpdateVisuals();

    private void UpdateVisuals()
    {
        OkIcon.Visibility = Verdict == true ? Visibility.Visible : Visibility.Collapsed;
        WarnIcon.Visibility = Verdict == false ? Visibility.Visible : Visibility.Collapsed;
        UnknownIcon.Visibility = Verdict is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
