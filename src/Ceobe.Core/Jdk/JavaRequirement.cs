using System.Collections.Concurrent;
using Ceobe.Core.Util;

namespace Ceobe.Core.Jdk;

/// <summary>
/// 某 MC 版本所需的 Java 主版本（取自 piston-meta 版本 JSON 的 <c>javaVersion.majorVersion</c>）。
/// 逐条对齐 Java 版 <c>JavaRequirement</c>：动态查询 + 兜底 21 + 进程内缓存；失败不阻塞流程。
///
/// ADR-011 的实测锚点（1.20.4→17、1.21.8→21、26.x→25）不是硬编码表，而是 Mojang 清单数据——
/// 动态查询自然得到；网络测试里逐条断言这些映射（见 tests/Jdk/JavaRequirementTest）。
///
/// 与 Java 版的实现差异：Java 版复用 <c>provider/MojangProvider.versionJson</c>；C# 版 Providers
/// 分包由他人并行迁移中，为避免跨包编译依赖，这里自带 manifest→versionJson 的最小查询。
/// Providers 落地后可收敛重复（遗留项，见交付报告）。
/// </summary>
public static class JavaRequirement
{
    /// <summary>Mojang 版本清单（与 Java 版 MojangProvider.MANIFEST 同值；镜像改写由 HttpUtil→Mirrors 自动完成）。</summary>
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>兜底：多数 1.20.5+ 版本需要 21（对齐 Java 版 FALLBACK）。</summary>
    private const int Fallback = 21;

    private static readonly ConcurrentDictionary<string, int> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 该 MC 版本所需的 Java 主版本。非 Mojang 版本（快照/拼写错误）或网络失败时返回兜底 21，
    /// 不抛异常（对齐 Java 版「不阻塞流程」语义）；用户取消经 CancellationToken 上抛。
    /// </summary>
    public static async Task<int> RequiredMajorAsync(string mcVersion, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(mcVersion, out var cached)) return cached;
        int major;
        try
        {
            major = await QueryMajorAsync(mcVersion, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            major = Fallback;
        }
        Cache[mcVersion] = major;
        return major;
    }

    private static async Task<int> QueryMajorAsync(string mcVersion, CancellationToken ct)
    {
        var manifest = await HttpUtil.GetStringAsync(ManifestUrl, ct).ConfigureAwait(false);
        string? versionUrl = null;
        using (var doc = JsonUtil.Parse(manifest))
        {
            if (doc.RootElement.TryGetProperty("versions", out var versions)
                && versions.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var v in versions.EnumerateArray())
                {
                    if (v.TryGetProperty("id", out var id)
                        && string.Equals(id.GetString(), mcVersion, StringComparison.Ordinal))
                    {
                        versionUrl = v.TryGetProperty("url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }
        }
        if (string.IsNullOrEmpty(versionUrl)) return Fallback; // 不在 Mojang 清单 → 兜底，不阻塞

        var json = await HttpUtil.GetStringAsync(versionUrl!, ct).ConfigureAwait(false);
        using var vj = JsonUtil.Parse(json);
        if (vj.RootElement.TryGetProperty("javaVersion", out var jv)
            && jv.TryGetProperty("majorVersion", out var mv)
            && mv.TryGetInt32(out var n) && n > 0)
        {
            return n;
        }
        return Fallback;
    }
}
