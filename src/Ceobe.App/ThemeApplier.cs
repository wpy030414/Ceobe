using Microsoft.UI.Xaml;

namespace Ceobe.App;

/// <summary>
/// 把 settings.json 的 theme 真正应用到界面。
///
/// 为什么需要这层：Java 版把主题存在 <c>AppSettings.theme</c> 里并由 JavaFX 的
/// theme-light/theme-dark 样式类切换；迁到 WinUI 后，设置对话框照样把值写进了
/// settings.json，但**没有任何地方去读它**——用户勾了「深色主题」却毫无变化。
/// 这是功能集合里的真实缺口，不是样式偏好问题。
///
/// 为什么设根元素的 RequestedTheme 而不是 Application.RequestedTheme：
/// 后者只在应用启动前可设，运行期切换无效。而 Window 不是 FrameworkElement，
/// 所以取 Window.Content 这个根元素。
/// </summary>
internal static class ThemeApplier
{
    public static void Apply(Window window, string? theme)
    {
        if (window.Content is FrameworkElement root) Apply(root, theme);
    }

    public static void Apply(FrameworkElement root, string? theme) =>
        root.RequestedTheme = theme?.Trim().ToLowerInvariant() switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            // 空/未知 = 跟随系统（Java 版只有 light/dark 两态，这里多一档无害）
            _ => ElementTheme.Default,
        };
}
