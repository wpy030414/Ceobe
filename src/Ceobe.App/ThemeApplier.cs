using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.Core.Store;

namespace Ceobe.App;

/// <summary>
/// 把 settings.json 的 theme 真正应用到界面。
///
/// 为什么需要这层：Java 版把主题存在 <c>AppSettings.theme</c> 里并由 JavaFX 的
/// theme-light/theme-dark 样式类切换；迁到 WinUI 后，设置对话框照样把值写进了
/// settings.json，但**没有任何地方去读它**——用户勾了「深色主题」却毫无变化。
///
/// 为什么设根元素的 RequestedTheme 而不是 Application.RequestedTheme：
/// 后者只在应用启动前可设，运行期切换无效。而 Window 不是 FrameworkElement，
/// 所以取 Window.Content 这个根元素。
///
/// 2026-10-22：支持三态（light / dark / system）。ContentDialog 位于 XamlRoot 的
/// PopupRoot，不在 Window.Content 的子树里，不会继承根元素的 RequestedTheme，
/// 因此必须显式 <see cref="ApplyToDialog"/>；系统标题栏的 caption 颜色也要单独同步。
/// </summary>
internal static class ThemeApplier
{
    /// <summary>当前应用的主题（Default = 跟随系统）。</summary>
    public static ElementTheme Current { get; private set; } = ElementTheme.Default;

    /// <summary>主题变化（运行期预览或启动应用）。供标题栏等窗口级元素同步。</summary>
    public static event Action? ThemeChanged;

    public static void Apply(Window window, string? theme)
    {
        if (window.Content is FrameworkElement root) Apply(root, theme);
    }

    public static void Apply(FrameworkElement root, string? theme)
    {
        Current = AppSettings.NormalizeTheme(theme) switch
        {
            AppSettings.ThemeDark => ElementTheme.Dark,
            AppSettings.ThemeLight => ElementTheme.Light,
            // "system" / 空 / 未知 = 跟随系统
            _ => ElementTheme.Default,
        };
        root.RequestedTheme = Current;
        ThemeChanged?.Invoke();
    }

    /// <summary>PopupRoot 不继承根元素主题，ContentDialog 必须显式设置。</summary>
    public static void ApplyToDialog(ContentDialog dialog) => dialog.RequestedTheme = Current;

    /// <summary>规范化的主题键，用于设置持久化/标题栏映射。</summary>
    public static string CurrentKey => Current switch
    {
        ElementTheme.Dark => "dark",
        ElementTheme.Light => "light",
        _ => "system",
    };
}
