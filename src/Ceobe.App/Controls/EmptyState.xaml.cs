using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ceobe.App.Controls;

/// <summary>
/// 可复用空状态。页面只负责给图标、标题、说明和一个可选动作按钮，
/// 不再各自手写一行居中的 TextBlock（那会随布局 bug 飘到可视区外）。
/// </summary>
public sealed partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyState),
        new PropertyMetadata("\uE946", (d, e) => ((EmptyState)d).Icon.Glyph = (string)e.NewValue));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) => ((EmptyState)d).TitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", OnDescriptionChanged));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(EmptyState),
        new PropertyMetadata(null, (d, e) => ((EmptyState)d).ActionHost.Content = e.NewValue));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>动作区：通常放一个 Button。</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public EmptyState()
    {
        InitializeComponent();
        Icon.Glyph = Glyph;
        TitleText.Text = Title;
        DescriptionText.Text = Description;
        DescriptionText.Visibility = string.IsNullOrEmpty(Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ActionHost.Content = ActionContent;
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (EmptyState)d;
        var text = (string)e.NewValue;
        control.DescriptionText.Text = text;
        control.DescriptionText.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
