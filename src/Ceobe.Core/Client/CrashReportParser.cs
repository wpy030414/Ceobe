using System.Text.RegularExpressions;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// 一次崩溃解析的结论。界面只呈现这四项，原始日志退居证据位（设计稿 06）。
/// </summary>
/// <param name="Parsed">是否识别出崩溃报告结构（false = 只是普通退出/日志为空）。</param>
/// <param name="Headline">一句话结论，如「Create 在初始化时崩溃」。</param>
/// <param name="ExceptionLine">原始异常行（等宽展示，供懂行的人核对）。</param>
/// <param name="Cause">人话原因：为什么崩、缺什么、影响到什么。</param>
/// <param name="SuspectMod">最可疑的 Mod id（可为空）。</param>
/// <param name="Suspects">报告里点名的全部可疑 Mod。</param>
/// <param name="Actions">可执行下一步（动词开头）。</param>
public sealed record CrashAnalysis(
    bool Parsed,
    string Headline,
    string? ExceptionLine,
    string Cause,
    string? SuspectMod,
    IReadOnlyList<string> Suspects,
    IReadOnlyList<string> Actions);

/// <summary>
/// crash-report 解析器（纯逻辑，无 IO）。
///
/// 这是客户端编排里最值得花钱的一块：HMCL / PCL 把日志原样贴给用户，
/// 而用户真正需要的是「哪个 Mod、为什么、怎么办」。解析放在 Core 而不是 ViewModel，
/// 因为 CLI 也要能 <c>ceobe client diagnose</c>，且这段规则需要单测覆盖各种真实崩溃文本。
///
/// 解析是**尽力而为**的启发式：格式随加载器/版本变化，宁可给出「不确定」的措辞，
/// 也不要把猜测说成结论（错误归因会让用户去删一个无辜的 Mod）。
/// </summary>
public static partial class CrashReportParser
{
    [GeneratedRegex(@"^(?:Caused by:\s*)?([A-Za-z_$][\w.$]*(?:Exception|Error|Throwable))(?::\s*(.*))?$")]
    private static partial Regex ExceptionLineRegex();

    [GeneratedRegex(@"^--\s+Mod\s+([A-Za-z0-9_\-]+)\s+--$")]
    private static partial Regex ModSectionRegex();

    [GeneratedRegex(@"Mod ID:\s*'([^']+)'")]
    private static partial Regex MissingDepIdRegex();

    [GeneratedRegex(@"Requested by:\s*'([^']+)'")]
    private static partial Regex MissingDepOwnerRegex();

    [GeneratedRegex(@"^\s*at\s+([a-z][\w]*)\.")]
    private static partial Regex StackPackageRegex();

    public static CrashAnalysis Parse(string? text, IReadOnlyCollection<string>? installedMods = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CrashAnalysis(false, "没有可解析的崩溃报告", null,
                "游戏进程退出了，但没有留下崩溃报告——通常是 JVM 层面的问题（Java 版本不符、内存参数非法或显卡驱动直接崩了）。",
                null, [], ["到「日志」分区看最后 50 行，确认退出前最后一条输出"]);
        }

        var lines = text.Split('\n');
        string? description = null;
        string? exceptionLine = null;
        string? exceptionType = null;
        string? exceptionMessage = null;
        var suspects = new List<string>();
        var missingDeps = new List<(string Id, string? Owner)>();
        var stackPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var inMissingDeps = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            if (description is null && trimmed.StartsWith("Description:", StringComparison.Ordinal))
            {
                description = trimmed["Description:".Length..].Trim();
                continue;
            }

            var modMatch = ModSectionRegex().Match(trimmed);
            if (modMatch.Success)
            {
                var id = modMatch.Groups[1].Value;
                // minecraft / forge / fabricloader 这些不是「用户装的 Mod」，不要当成嫌疑人
                if (!IsPlatformId(id) && !suspects.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    suspects.Add(id);
                }
                continue;
            }

            if (trimmed.StartsWith("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase))
            {
                inMissingDeps = true;
                continue;
            }

            if (inMissingDeps)
            {
                var idm = MissingDepIdRegex().Match(trimmed);
                if (idm.Success)
                {
                    var owner = MissingDepOwnerRegex().Match(trimmed);
                    missingDeps.Add((idm.Groups[1].Value, owner.Success ? owner.Groups[1].Value : null));
                    continue;
                }
                if (trimmed.Length == 0) inMissingDeps = false;
            }

            if (trimmed.StartsWith("Suspected Mods:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in trimmed["Suspected Mods:".Length..].Split(','))
                {
                    var id = part.Trim().Split(' ')[0];
                    if (id.Length > 0 && !IsPlatformId(id)
                        && !suspects.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        suspects.Add(id);
                    }
                }
                continue;
            }

            if (exceptionLine is null)
            {
                var m = ExceptionLineRegex().Match(trimmed);
                if (m.Success)
                {
                    exceptionType = m.Groups[1].Value;
                    exceptionMessage = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                    exceptionLine = trimmed;
                }
            }

            // 栈顶的包名是第二层线索：异常类名本身可能来自 JDK，但调用者一定在某个 Mod 里
            if (trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                var pm = StackPackageRegex().Match(trimmed);
                if (pm.Success) stackPackages.Add(pm.Groups[1].Value);
            }
        }

        // 嫌疑 Mod 收敛：报告点名的优先；只有一处时直接用；否则按「包名能对上 Mod id」筛一遍
        if (suspects.Count == 0 && installedMods is { Count: > 0 })
        {
            foreach (var pkg in stackPackages)
            {
                var hit = installedMods.FirstOrDefault(m =>
                    string.Equals(m, pkg, StringComparison.OrdinalIgnoreCase));
                if (hit is not null && !suspects.Contains(hit, StringComparer.OrdinalIgnoreCase))
                {
                    suspects.Add(hit);
                }
            }
        }

        var suspect = suspects.Count > 0 ? suspects[0] : null;
        var (cause, actions) = Explain(exceptionType, exceptionMessage, missingDeps, suspect, suspects);
        var headline = BuildHeadline(description, suspect, exceptionType);

        return new CrashAnalysis(true, headline, exceptionLine, cause, suspect, suspects, actions);
    }

    private static string BuildHeadline(string? description, string? suspect, string? exceptionType)
    {
        var where = suspect is not null ? $"{suspect} " : "";
        if (!string.IsNullOrWhiteSpace(description))
        {
            // Description 常是 "Rendering overlay" / "Initializing game" 这类阶段名
            return $"{where}在「{description}」阶段崩溃";
        }
        if (exceptionType is not null) return $"{where}抛出 {ShortType(exceptionType)}";
        return "游戏启动失败";
    }

    private static (string Cause, List<string> Actions) Explain(
        string? type,
        string? message,
        List<(string Id, string? Owner)> missingDeps,
        string? suspect,
        List<string> suspects)
    {
        var actions = new List<string>();
        string cause;

        if (missingDeps.Count > 0)
        {
            var first = missingDeps[0];
            cause = first.Owner is not null
                ? $"{first.Owner} 声明需要前置 {first.Id}，但 mods 目录里没有它。缺前置时加载器会在静态初始化阶段直接放弃，游戏连主菜单都到不了。"
                : $"缺少前置 Mod {first.Id}。缺前置时加载器会在初始化阶段直接放弃。";
            actions.Add($"安装 {first.Id}" + (first.Owner is not null ? $"（{first.Owner} 需要）" : ""));
            if (first.Owner is not null) actions.Add($"或者停用 {first.Owner}，连带停用它依赖的附属 Mod");
            foreach (var extra in missingDeps.Skip(1).Take(3))
            {
                actions.Add($"一并安装 {extra.Id}" + (extra.Owner is not null ? $"（{extra.Owner} 需要）" : ""));
            }
            return (cause, actions);
        }

        switch (type)
        {
            case "java.lang.OutOfMemoryError":
            case "java.lang.OutOfMemoryError: Java heap space":
                cause = "堆内存不够。整合包 + 光影很容易超过默认分配，JVM 在 GC 反复失败后直接抛错退出。";
                actions.Add("把内存上限调到 6144 MB 或更高");
                actions.Add("如果是光影整合包，先把光影关掉确认能进游戏");
                return (cause, actions);

            case "java.lang.UnsupportedClassVersionError":
                cause = $"当前 Java 版本低于这个 Mod / 游戏所需。报错里会写明 class file version，{message ?? "通常意味着需要更高的主版本"}。";
                actions.Add("在「设置」里换用与游戏版本匹配的 Java（1.20.5+ 需要 Java 21）");
                return (cause, actions);

            case "java.lang.NoClassDefFoundError":
            case "java.lang.ClassNotFoundException":
            {
                var hint = GuessPackageMod(message);
                cause = message is not null
                    ? $"需要的类 {message} 不存在。"
                      + (hint is not null
                          ? $"这个类属于 {hint} 的包，说明对应前置没有装（或版本不对）。"
                          : "常见原因是前置 Mod 缺失、或 Mod 与当前游戏/加载器版本不匹配。")
                    : "某个 Mod 需要的类不存在，通常是前置缺失或版本不匹配。";
                if (hint is not null) actions.Add($"补齐缺失前置：安装提供 {hint} 的 Mod");
                actions.Add("到 Mod 分区看「依赖健康」列，把标红的缺失前置补齐");
                if (suspect is not null) actions.Add($"若补齐仍崩，停用 {suspect} 再试");
                return (cause, actions);
            }

            case "java.lang.NoSuchMethodError":
            case "java.lang.NoSuchFieldError":
                cause = "Mod 调用了不存在的方法/字段——几乎总是「Mod 版本与游戏或前置版本不匹配」，而不是文件损坏。";
                actions.Add("检查报错 Mod 的支持版本是否覆盖当前游戏版本");
                if (suspect is not null) actions.Add($"把 {suspect} 换成本游戏版本对应的版本");
                return (cause, actions);

            case "net.fabricmc.loader.api.EntrypointException":
            case "net.fabricmc.loader.impl.FormattedException":
                cause = "Fabric 在加载入口点时失败。报告里的 Mod 段落指明了是哪一个。";
                if (suspect is not null) actions.Add($"先停用 {suspect}，确认能进主菜单后再逐个排查");
                return (cause, actions);

            case "java.lang.RuntimeException" when message?.Contains("mixin", StringComparison.OrdinalIgnoreCase) == true:
                cause = "Mixin 注入失败：Mod 想改的游戏代码在目标版本里已经变了。";
                if (suspect is not null) actions.Add($"停用 {suspect}，或换用支持当前游戏版本的版本");
                return (cause, actions);
        }

        if (type is not null && type.Contains("OpenGL", StringComparison.OrdinalIgnoreCase)
            || message?.Contains("Pixel format not accelerated", StringComparison.OrdinalIgnoreCase) == true
            || message?.Contains("GLFW", StringComparison.OrdinalIgnoreCase) == true)
        {
            cause = "显卡/驱动层面的失败，游戏拿不到可用的 OpenGL 上下文。";
            actions.Add("更新显卡驱动");
            actions.Add("关掉光影后再启动");
            return (cause, actions);
        }

        if (type is null)
        {
            cause = "报告里没有可识别的异常行，可能是 JVM 启动参数错误或进程被外部结束。";
            actions.Add("检查「设置」里的 JVM 参数是否有拼写错误");
            actions.Add("看日志最后 50 行确认退出前发生了什么");
            return (cause, actions);
        }

        cause = suspects.Count > 1
            ? $"报告点名了 {suspects.Count} 个可疑 Mod（{string.Join("、", suspects.Take(3))}），但没有明确的缺失依赖声明。"
            : "游戏抛出了未被 Mod 自行处理的异常，报告未明确指出责任方。";
        actions.Add("按「最近安装/更新」顺序，逐个停用 Mod 二分定位");
        actions.Add("拿到完整报告后可到 Mod 分区查看依赖健康");
        return (cause, actions);
    }

    /// <summary>
    /// 从缺失类的包路径猜出「这个类属于哪个 Mod」。
    /// <c>com/jozufozu/flywheel/api/KineticBlockEntity</c> → <c>flywheel</c>：
    /// 去掉 com/org/net 这类公共前缀、api/impl/mixin 这类功能目录，再取**最内层**的包名
    /// （越靠内越可能是 Mod 自己的包，而不是作者的组织名）。
    /// 猜错时只在文案里多一句提示，不影响其它结论，故可以接受启发式。
    /// </summary>
    private static string? GuessPackageMod(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        var segments = className.Replace('.', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "com", "org", "net", "io", "java", "javax", "jdk", "sun", "api", "impl",
            "mixin", "mixins", "client", "server", "common", "util", "utils", "core", "internal",
        };
        string? candidate = null;
        foreach (var seg in segments)
        {
            if (common.Contains(seg)) continue;
            if (seg.Length > 0 && char.IsUpper(seg[0])) break;   // 到了类名，停止
            candidate = seg;                                     // 持续覆盖 → 取最内层
        }
        return candidate;
    }

    private static bool IsPlatformId(string id) =>
        id.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
        || id.Equals("forge", StringComparison.OrdinalIgnoreCase)
        || id.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
        || id.Equals("fabricloader", StringComparison.OrdinalIgnoreCase)
        || id.Equals("fabric", StringComparison.OrdinalIgnoreCase)
        || id.Equals("java", StringComparison.OrdinalIgnoreCase)
        || id.Equals("quilt_loader", StringComparison.OrdinalIgnoreCase);

    private static string ShortType(string full) => full[(full.LastIndexOf('.') + 1)..];
}

/// <summary>
/// 崩溃报告与日志文件的定位/读取。目录约定与 <c>.minecraft</c> 一致，
/// 用户从别的启动器搬过来的实例也能直接命中。
/// </summary>
public static class CrashReports
{
    /// <summary>最新一份崩溃报告；没有则 null。</summary>
    public static string? Latest(string clientName)
    {
        var dir = ClientStore.CrashReportsDir(clientName);
        try
        {
            return Directory.EnumerateFiles(dir, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Path, DateTimeOffset At)> Recent(string clientName, int count)
    {
        var dir = ClientStore.CrashReportsDir(clientName);
        try
        {
            return Directory.EnumerateFiles(dir, "crash-*.txt")
                .Select(p => (p, new DateTimeOffset(File.GetLastWriteTimeUtc(p), TimeSpan.Zero)))
                .OrderByDescending(t => t.Item2)
                .Take(count)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string? ReadText(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>读日志尾部（默认 60 行）。文件可能正被游戏进程写入，故用 ReadWrite 共享打开。</summary>
    public static IReadOnlyList<string> Tail(string path, int lines = 60)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var all = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                all.Add(line);
                if (all.Count > lines * 4) all.RemoveRange(0, all.Count - lines * 2);
            }
            return all.Count <= lines ? all : all[^lines..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
