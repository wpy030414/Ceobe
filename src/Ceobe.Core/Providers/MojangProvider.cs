using System.Globalization;
using System.Text.Json;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// 原版服务端（Mojang piston-meta）。
/// <para>契约：manifest v2 → 版本 JSON → <c>downloads.server.{url,sha1,size}</c>。</para>
/// </summary>
public sealed class MojangProvider : IServerJarProvider
{
    internal const string Manifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly SemaphoreSlim ReleaseGate = new(1, 1);
    private static readonly TimeSpan ReleaseCacheTtl = TimeSpan.FromMinutes(30);
    private static IReadOnlyList<GameVersionInfo>? _releaseCache;
    private static DateTimeOffset _releaseCacheAt;

    public ServerType Type => ServerType.VANILLA;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default) =>
        (await ListReleasesAsync(ct).ConfigureAwait(false)).Select(v => v.Id).ToList();

    /// <summary>
    /// 正式版清单（含发布日期）。GUI 的版本下拉用它把「版本号 + 发布日期」一次取全；
    /// 仍然只收 type=release，快照不进入默认创建路径。
    ///
    /// 性能：清单 30 分钟内进程内缓存；BMCLAPI 的 manifest 端点实测可能 10s+ 或空响应，
    /// 因此只给镜像 2 秒预算，超时/空响应立刻回退直连（本机直连约 0.7s）。
    /// </summary>
    public static async Task<IReadOnlyList<GameVersionInfo>> ListReleasesAsync(CancellationToken ct = default)
    {
        if (TryGetCachedReleases(out var cached)) return cached;

        await ReleaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCachedReleases(out cached)) return cached;

            var json = await HttpUtil.GetStringAsync(Manifest, ct, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            using var doc = JsonUtil.Parse(json);
            var list = new List<GameVersionInfo>();
            foreach (var v in doc.RootElement.Prop("versions").Items())
            {
                if (v.Prop("type").AsText() != "release") continue;
                var id = v.Prop("id").AsText() ?? "";
                if (id.Length == 0) continue;
                DateTimeOffset? released = DateTimeOffset.TryParse(
                    v.Prop("releaseTime").AsText(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dt)
                    ? dt
                    : null;
                list.Add(new GameVersionInfo(id, released));
            }
            list.Sort((a, b) => VersionUtil.Descending.Compare(a.Id, b.Id));

            _releaseCache = list;
            _releaseCacheAt = DateTimeOffset.UtcNow;
            return list;
        }
        finally
        {
            ReleaseGate.Release();
        }
    }

    private static bool TryGetCachedReleases(out IReadOnlyList<GameVersionInfo> releases)
    {
        var cached = _releaseCache;
        if (cached is not null && DateTimeOffset.UtcNow - _releaseCacheAt < ReleaseCacheTtl)
        {
            releases = cached;
            return true;
        }
        releases = [];
        return false;
    }

    public async Task<InstalledServer> InstallAsync(
        string instanceDir, string gameVersion, string? loaderVersion,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var entryUrl = await FindVersionUrlAsync(gameVersion, ct).ConfigureAwait(false);
        if (entryUrl is null) throw CeobeException.Of("Mojang 无此版本: {0}", gameVersion);

        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(entryUrl, ct).ConfigureAwait(false));
        var server = doc.RootElement.Prop("downloads").Prop("server");
        if (server.IsMissing())
        {
            throw CeobeException.Of("版本 {0} 无服务端下载（可能过旧）", gameVersion);
        }
        var url = server.Prop("url").AsText() ?? "";
        var sha1 = server.Prop("sha1").AsText(null);
        var size = server.Prop("size").AsLong(-1);

        ProviderSupport.Report(progress, "下载原版服务端 " + gameVersion, 0, size);
        var cached = await ProviderSupport.CachedJarAsync(
            "vanilla-" + gameVersion + ".jar", url, sha1, null, progress, ct).ConfigureAwait(false);
        var target = Path.Combine(instanceDir, "server.jar");
        ProviderSupport.Copy(cached, target);
        ProviderSupport.Report(progress, "安装完成", size, size);
        return new InstalledServer("server.jar", gameVersion, null);
    }

    private static async Task<string?> FindVersionUrlAsync(string gameVersion, CancellationToken ct)
    {
        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(Manifest, ct).ConfigureAwait(false));
        foreach (var v in doc.RootElement.Prop("versions").Items())
        {
            if (gameVersion == v.Prop("id").AsText()) return v.Prop("url").AsText();
        }
        return null;
    }

    /// <summary>
    /// 该版本的版本 JSON（piston-meta），含 downloads 与 javaVersion。
    /// 对齐 Java <c>versionJson</c>；返回 DOM 由调用方负责 Dispose（JsonDocument 生命周期归调用方）。
    /// </summary>
    public static async Task<JsonDocument> VersionJsonAsync(string gameVersion, CancellationToken ct = default)
    {
        var url = await FindVersionUrlAsync(gameVersion, ct).ConfigureAwait(false);
        if (url is null) throw CeobeException.Of("Mojang 无此版本: {0}", gameVersion);
        return JsonUtil.Parse(await HttpUtil.GetStringAsync(url, ct).ConfigureAwait(false));
    }
}
