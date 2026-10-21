using System.Text.Json.Serialization;
using Ceobe.Core.Util;

namespace Ceobe.Core.Store;

/// <summary>
/// 应用级设置（<c>&lt;home&gt;/settings.json</c>），key 与 Java 版 Jackson 输出一致。
///
/// 整改项 3：训练员反馈「服务器存放位置貌似无法修改地址（它默认到了我的 C 盘）」。
/// Java 版 <c>AppSettings</c> 只有 <c>theme</c> 一个字段，<c>CeobeHome</c> 也不认设置文件，
/// 故这里新增 <see cref="HomePath"/>；其生效路径是 <see cref="Apply"/> → <see cref="CeobeHome.Override"/>。
/// </summary>
public sealed class AppSettings
{
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    /// <summary>主题：light | dark | system（默认跟随系统）。</summary>
    public string Theme { get; set; } = ThemeSystem;

    /// <summary>把任意历史值收敛到三态；未知/空一律跟随系统。</summary>
    public static string NormalizeTheme(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        ThemeLight => ThemeLight,
        ThemeDark => ThemeDark,
        _ => ThemeSystem,
    };

    /// <summary>
    /// 数据根目录（实例、缓存、自管 JDK 都在它下面）。
    /// null / 空 = 默认 <c>%USERPROFILE%\.ceobe</c>。环境变量 <c>CEOBE_HOME</c> 优先级更高（测试用）。
    /// </summary>
    public string? HomePath { get; set; }

    /// <summary>上一次由 GUI 选择的「服务器默认存放盘/目录」，仅用于记忆，不参与路径解析。</summary>
    public string? LastInstallDir { get; set; }

    /// <summary>整改项 2：新建实例时白名单默认值。Java 版把 white-list 默认设 true 又没给入口，
    /// 导致新手开服后朋友进不来。这里改为 false，创建向导会显式询问一次。</summary>
    public bool DefaultWhitelistEnabled { get; set; }

    [JsonIgnore]
    public bool IsDark => string.Equals(Theme, "dark", StringComparison.OrdinalIgnoreCase);

    /// <summary>settings.json 路径（用当前解析出的 home，因此读它自身不需要 HomePath）。</summary>
    public static string SettingsFilePath() => System.IO.Path.Combine(CeobeHome.Root(), "settings.json");

    public static AppSettings Load()
    {
        var f = SettingsFilePath();
        if (!File.Exists(f)) return new AppSettings();
        try
        {
            return JsonUtil.Parse<AppSettings>(System.IO.File.ReadAllText(f, new System.Text.UTF8Encoding(false)))
                   ?? new AppSettings();
        }
        catch (Exception)
        {
            // 设置损坏不致命：回落默认（对齐 Java 版 catch 后 return new）
            return new AppSettings();
        }
    }

    /// <summary>落盘失败不致命（对齐 Java 版）；返回是否成功供 GUI 决定要不要提示。</summary>
    public bool Save()
    {
        var f = SettingsFilePath();
        try
        {
            CeobeHome.Ensure(CeobeHome.Root());
            System.IO.File.WriteAllText(f, JsonUtil.Write(this), new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把 <see cref="HomePath"/> 应用到进程内路径解析。
    /// 必须在任何实例操作之前调用一次（GUI/CLI 启动时）。
    /// </summary>
    public void Apply() => CeobeHome.Override = string.IsNullOrWhiteSpace(HomePath) ? null : HomePath;

    /// <summary>
    /// 整改项 3 的红字警告：%USERPROFILE% 若被 OneDrive「已知文件夹重定向」接管，
    /// 世界文件的高频写入会把云盘打爆（Windows 专属新雷，Java 版不会遇到）。
    /// </summary>
    public static bool IsPathUnderOneDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrWhiteSpace(oneDrive)
                && full.StartsWith(System.IO.Path.GetFullPath(oneDrive),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return true;
            }
            return full.Contains(System.IO.Path.DirectorySeparatorChar + "OneDrive" + System.IO.Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
