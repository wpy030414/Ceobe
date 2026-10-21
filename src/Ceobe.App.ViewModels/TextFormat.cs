namespace Ceobe.App.ViewModels;

/// <summary>
/// 界面文案格式化工具。
///
/// 提成 public 独立类而非藏在某个 ViewModel 里：<see cref="HumanSize"/> 被导入页与设置页
/// （搬家体量预览）共用，View 工程也要调它——internal 挂在 ImportViewModel 下会导致
/// 跨程序集访问不到，且「设置页调 ImportViewModel.HumanSize」语义也别扭。
/// </summary>
public static class TextFormat
{
    /// <summary>字节数 → 人类可读体积（B / KB / MB / GB，保留一位小数）。</summary>
    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#") + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1024d / 1024d).ToString("0.#") + " MB",
        _ => (bytes / 1024d / 1024d / 1024d).ToString("0.##") + " GB",
    };
}
