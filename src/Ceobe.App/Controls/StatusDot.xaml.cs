using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.Core.Model;

namespace Ceobe.App.Controls;

/// <summary>
/// 实例状态圆点：运行中绿、启动/停止过渡琥珀、崩溃红、其余中性灰。
/// 颜色只是辅助——旁边必须有文字状态（WCAG 1.4.1：不能只靠颜色传达信息）。
/// </summary>
public sealed partial class StatusDot : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(InstanceState), typeof(StatusDot),
        new PropertyMetadata(InstanceState.STOPPED, OnStateChanged));

    public InstanceState State
    {
        get => (InstanceState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public StatusDot()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusDot)d).UpdateVisuals();

    private void UpdateVisuals()
    {
        var live = State is InstanceState.RUNNING;
        var busy = State is InstanceState.STARTING or InstanceState.STOPPING;
        var crashed = State is InstanceState.CRASHED;

        LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        BusyDot.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CrashedDot.Visibility = crashed ? Visibility.Visible : Visibility.Collapsed;
        IdleDot.Visibility = !live && !busy && !crashed ? Visibility.Visible : Visibility.Collapsed;
    }
}
