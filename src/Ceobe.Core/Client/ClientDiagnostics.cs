using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// 把 Ceobe 的服务端实例翻译成「客户端多人列表条目」。
///
/// 这是客户端编排与服务端编排的接缝，也是整套设计里唯一没有先例可抄的部分：
/// 服务端实例的穿透地址（Pinggy / Cloudflare / Tailscale）在实例元数据里已经有了，
/// 客户端只需要把它写进 <c>servers.dat</c>。没有这一层，用户就得手动复制 IP——
/// 而这恰恰是「用 ceobe 开服给朋友玩」这条主路径上最烦的一步。
/// </summary>
public static class ClientServerLinks
{
    private const string LocalHost = "127.0.0.1";

    /// <summary>
    /// 为某个客户端实例收集应写入的服务器：
    /// 配对的服务端**总是**包含（没有穿透地址时用本机回环，方便单机联调）；
    /// 其余服务端只在有可用公网地址时才写入——空地址会变成一条点不进去的死条目。
    /// </summary>
    public static List<ServerEntry> Collect(ClientMeta meta)
    {
        var result = new List<ServerEntry>();
        foreach (var server in LoadServers())
        {
            var isPaired = string.Equals(server.Name, meta.PairedServer, StringComparison.Ordinal);
            var address = ServersDatFile.NormalizeAddress(server.LastPinggyAddress)
                          ?? ServersDatFile.NormalizeAddress(server.LastCloudflareUrl);
            if (address is null && !isPaired) continue;
            result.Add(new ServerEntry(server.Name, address ?? $"{LocalHost}:{server.Port}"));
        }
        return result;
    }

    /// <summary>全部服务端实例（供「服务器」分区列表使用，含未配置穿透的）。</summary>
    public static IReadOnlyList<InstanceMeta> LoadServers()
    {
        var list = new List<InstanceMeta>();
        try
        {
            var root = CeobeHome.InstancesDir();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "instance.json"))) continue;
                try
                {
                    list.Add(InstanceManager.ReadMeta(dir));
                }
                catch (CeobeException)
                {
                    // 单个实例元数据损坏不影响其它条目
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return list;
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>某服务端对外的可用地址（穿透优先，其次回环）；不可达时为 null。</summary>
    public static string? AddressOf(InstanceMeta server) =>
        ServersDatFile.NormalizeAddress(server.LastPinggyAddress)
        ?? ServersDatFile.NormalizeAddress(server.LastCloudflareUrl)
        ?? $"{LocalHost}:{server.Port}";
}

public enum DiagnosticLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>一条启动前体检结论。</summary>
public sealed record Diagnostic(DiagnosticLevel Level, string Title, string Detail, string? Action);

/// <summary>
/// 启动前体检。设计稿把这条黄色横幅放在启动页顶部，理由是：
/// 「在点启动前就知道会崩」比「进游戏黑屏、再翻日志」有价值得多。
///
/// 检查项刻意只保留**确定性强**的：文件是否齐、缺哪个前置、Java 是否能解析、
/// 与服务端版本是否一致。不做「猜测型」告警（比如「这个 Mod 可能不兼容」），
/// 因为误报会让用户学会无视横幅，那这条设计就白做了。
/// </summary>
public static class ClientDiagnostics
{
    public static IReadOnlyList<Diagnostic> Check(ClientMeta meta)
    {
        var list = new List<Diagnostic>();

        CheckInstall(meta, list);
        CheckMods(meta, list);
        CheckJava(meta, list);
        CheckPairedServer(meta, list);
        CheckMemory(meta, list);

        // 错误在前，其次警告，最后提示
        return list
            .OrderByDescending(d => d.Level)
            .ToList();
    }

    private static void CheckInstall(ClientMeta meta, List<Diagnostic> list)
    {
        var json = ClientVersion.VersionJson(meta.GameVersion);
        if (!File.Exists(json))
        {
            list.Add(new Diagnostic(DiagnosticLevel.Error, "游戏文件不完整",
                $"{meta.GameVersion} 的版本清单缺失，游戏无法启动。",
                "点「修复游戏文件」重新装配"));
            return;
        }

        var jar = ClientVersion.ClientJar(meta.GameVersion);
        if (!File.Exists(jar))
        {
            list.Add(new Diagnostic(DiagnosticLevel.Error, "缺少客户端 jar",
                $"{meta.GameVersion}.jar 不在缓存里。",
                "点「修复游戏文件」重新下载"));
        }

        if (meta.Loader != ClientLoader.VANILLA)
        {
            var loaderId = ClientVersion.LoaderVersionId(meta);
            if (!File.Exists(ClientVersion.VersionJson(loaderId)))
            {
                list.Add(new Diagnostic(DiagnosticLevel.Error, $"缺少 {meta.Loader.Label()} 配置",
                    $"加载器 profile（{loaderId}）不存在，启动会退回原版或直接失败。",
                    "点「修复游戏文件」重新装加载器"));
            }
        }

        var natives = ClientStore.NativesDir(meta.Name);
        var hasNative = Directory.Exists(natives)
                        && Directory.EnumerateFiles(natives).Any();
        if (!hasNative)
        {
            list.Add(new Diagnostic(DiagnosticLevel.Error, "natives 未解压",
                "游戏启动会报 UnsatisfiedLinkError（找不到 lwjgl 的本地库）。",
                "点「修复游戏文件」重新解压"));
        }
    }

    private static void CheckMods(ClientMeta meta, List<Diagnostic> list)
    {
        if (meta.Loader == ClientLoader.VANILLA) return;
        var mods = ClientModScanner.Scan(meta.Name);
        if (mods.Count == 0) return;

        var broken = mods.Where(m => m.Enabled && m.Missing.Count > 0).ToList();
        if (broken.Count == 0) return;

        var first = broken[0];
        var missing = broken.SelectMany(m => m.Missing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.Add(new Diagnostic(DiagnosticLevel.Error, "依赖诊断",
            $"{first.DisplayName ?? first.FileName} 缺少前置 {string.Join("、", first.Missing)}"
            + (broken.Count > 1 ? $"；另有 {broken.Count - 1} 个 Mod 也有缺失" : ""),
            $"安装 {string.Join("、", missing.Take(3))}"));
    }

    private static void CheckJava(ClientMeta meta, List<Diagnostic> list)
    {
        try
        {
            var version = ClientVersion.Resolve(meta);
            var settings = ClientSettings.Load();
            var home = string.IsNullOrWhiteSpace(meta.JavaHome) ? settings.JavaHome : meta.JavaHome;
            var resolution = Jdk.JavaResolver.Resolve(meta.GameVersion, version.JavaMajor, home);
            if (!resolution.Ok)
            {
                list.Add(new Diagnostic(DiagnosticLevel.Error, "Java 运行时不可用",
                    resolution.Message ?? $"需要 Java {version.JavaMajor}",
                    "在「设置」里选择或安装匹配的 JDK"));
            }
        }
        catch (CeobeException e)
        {
            // 版本文件缺失已在 CheckInstall 报过，这里不重复刷两条错误
            _ = e;
        }
    }

    private static void CheckPairedServer(ClientMeta meta, List<Diagnostic> list)
    {
        if (string.IsNullOrWhiteSpace(meta.PairedServer)) return;
        var server = ClientServerLinks.LoadServers()
            .FirstOrDefault(s => string.Equals(s.Name, meta.PairedServer, StringComparison.Ordinal));
        if (server is null)
        {
            list.Add(new Diagnostic(DiagnosticLevel.Warning, "配对的服务端不存在",
                $"「{meta.PairedServer}」已被删除或改名，加入服务器的快捷入口会失效。",
                "在「服务器」分区重新选择配对实例"));
            return;
        }

        if (!string.Equals(server.GameVersion, meta.GameVersion, StringComparison.Ordinal))
        {
            list.Add(new Diagnostic(DiagnosticLevel.Warning, "与服务端版本不一致",
                $"客户端是 {meta.GameVersion}，服务端「{server.Name}」是 {server.GameVersion}，"
                + "直接加入会被服务器拒绝（版本握手失败）。",
                $"用 {server.GameVersion} 的实例加入，或改服务端版本"));
        }
    }

    private static void CheckMemory(ClientMeta meta, List<Diagnostic> list)
    {
        var mods = meta.Loader == ClientLoader.VANILLA ? 0 : ClientModScanner.Scan(meta.Name).Count;
        if (mods > 20 && meta.MemoryMb < 4096)
        {
            list.Add(new Diagnostic(DiagnosticLevel.Warning, "内存分配偏低",
                $"已装 {mods} 个 Mod，但只分配了 {meta.MemoryMb} MB；整合包常见崩法是 GC 抖动后 OutOfMemoryError。",
                "把内存调到 4096 MB 或更高"));
        }
    }
}
