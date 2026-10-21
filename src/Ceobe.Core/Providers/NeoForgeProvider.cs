using System.Text.RegularExpressions;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// NeoForge（1.20.2+ 的 Forge 分叉，maven.neoforged.net）。
///
/// <para>安装：下载 <c>neoforge-&lt;ver&gt;-installer.jar</c> 并运行 <c>--installServer &lt;dir&gt;</c>；
/// 产物无单一启动 jar，改用参数文件启动：
/// <c>java @libraries/net/neoforged/neoforge/&lt;ver&gt;/&lt;win|unix&gt;_args.txt nogui</c>。</para>
///
/// <para>版本映射：NeoForge 版本 = <c>&lt;归一化 MC 版本&gt;.&lt;build&gt;</c>，1.x 去掉前导 "1."（1.21.8 → 21.8）、
/// 26.x 保留（26.3 → 26.3）。</para>
/// </summary>
public sealed partial class NeoForgeProvider : IServerJarProvider
{
    private const string Maven = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    // maven-metadata.xml 是 XML，用正则取 <version>（不引入 XML 依赖，对齐 Java）
    [GeneratedRegex("<version>([^<]+)</version>")]
    private static partial Regex VersionTag();

    public ServerType Type => ServerType.NEOFORGE;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        var neo = await NeoVersionsAsync(ct).ConfigureAwait(false);
        var outList = new List<string>();
        foreach (var mc in await new MojangProvider().ListVersionsAsync(ct).ConfigureAwait(false))
        {
            // Mc 列表已是降序，过滤后仍降序
            var p = Prefix(mc);
            if (SupportedPrefix(p) && neo.Any(v => v.StartsWith(p + ".", StringComparison.Ordinal)))
            {
                outList.Add(mc);
            }
        }
        return outList;
    }

    public async Task<IReadOnlyList<string>> ListLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
    {
        var p = Prefix(gameVersion) + ".";
        var outList = new List<string>();
        foreach (var v in await NeoVersionsAsync(ct).ConfigureAwait(false))
        {
            if (v.StartsWith(p, StringComparison.Ordinal)) outList.Add(v);
        }
        // 按 build 号降序（字符串序会把 "0.9" 排在 "0.83" 之前）；OrderByDescending 稳定，对齐 Java
        return outList.OrderByDescending(v => BuildNumber(v, p)).ToList();
    }

    public async Task<InstalledServer> InstallAsync(
        string instanceDir, string gameVersion, string? loaderVersion,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        string loader;
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var candidates = await ListLoaderVersionsAsync(gameVersion, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                throw new CeobeException("NeoForge 无适配 " + gameVersion + " 的版本");
            }
            loader = candidates[0];
        }
        else
        {
            loader = loaderVersion;
        }

        var installerUrl = $"{Maven}/{loader}/neoforge-{loader}-installer.jar";
        ProviderSupport.Report(progress, "下载 NeoForge 安装器 " + loader, 0, -1);
        var installer = await ProviderSupport.CachedJarAsync(
            "neoforge-installer-" + loader + ".jar", installerUrl,
            await TrySha1Async(installerUrl + ".sha1", ct).ConfigureAwait(false), null, progress, ct)
            .ConfigureAwait(false);

        ProviderSupport.Report(progress, "运行 NeoForge 安装器（含下载原版与库，可能较慢）", 0, -1);
        await ProviderSupport.RunCapturedAsync(
            "NeoForge 安装器",
            ProviderSupport.JavaBinary(),
            new[] { "-jar", Path.GetFullPath(installer), "--installServer", Path.GetFullPath(instanceDir) },
            instanceDir, ct).ConfigureAwait(false);

        var argsRel = $"libraries/net/neoforged/neoforge/{loader}/{(OperatingSystem.IsWindows() ? "win" : "unix")}_args.txt";
        if (!File.Exists(Path.Combine(instanceDir, argsRel)))
        {
            throw new CeobeException("NeoForge 安装未产出 " + argsRel + "，请检查安装器输出");
        }
        ProviderSupport.Report(progress, "安装完成", 1, 1);
        return new InstalledServer(argsRel, gameVersion, loader, argsRel);
    }

    /// <summary>归一化：1.x 去掉前导 "1."，其余原样。
    /// Java 版为包私有 static（供同包测试访问）；C# 测试在独立程序集，改 public（差异记录于交付报告）。</summary>
    public static string Prefix(string mcVersion)
    {
        return mcVersion.StartsWith("1.", StringComparison.Ordinal) ? mcVersion[2..] : mcVersion;
    }

    /// <summary>NeoForge 仅支持 MC ≥ 1.20.2，用主版本号下限过滤掉伪匹配（如 "1.0" → "0"）。</summary>
    private static bool SupportedPrefix(string prefix)
    {
        var dot = prefix.IndexOf('.');
        if (dot <= 0) return false;
        return int.TryParse(prefix[..dot], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var major) && major >= 20;
    }

    /// <summary>maven-metadata.xml 的版本集合（保持原始出现顺序）。</summary>
    private static async Task<IReadOnlyList<string>> NeoVersionsAsync(CancellationToken ct)
    {
        var xml = await HttpUtil.GetStringAsync(Maven + "/maven-metadata.xml", ct).ConfigureAwait(false);
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in VersionTag().Matches(xml))
        {
            if (set.Add(m.Groups[1].Value)) seen.Add(m.Groups[1].Value);
        }
        return seen;
    }

    /// <summary>前缀后的末段数字（build 号）；解析失败记 0，对齐 Java。</summary>
    private static long BuildNumber(string version, string prefix)
    {
        var suffix = version[prefix.Length..];
        var dot = suffix.LastIndexOf('.');
        var tail = dot >= 0 ? suffix[(dot + 1)..] : suffix;
        return long.TryParse(tail, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0L;
    }

    /// <summary>
    /// 尽力而为地取 .sha1 校验值：任何网络/HTTP 失败退化为仅下载（对齐 Java trySha1）。
    /// 语义差异：取消（OperationCanceledException）不吞——用户主动中断必须上抛，Java 同步版无此形态。
    /// </summary>
    private static async Task<string?> TrySha1Async(string url, CancellationToken ct)
    {
        try
        {
            var s = (await HttpUtil.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
            var sp = s.IndexOf(' ');
            return (sp > 0 ? s[..sp] : s).Trim();
        }
        catch (CeobeException)
        {
            return null;
        }
    }
}
