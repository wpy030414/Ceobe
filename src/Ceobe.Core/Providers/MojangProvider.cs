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

    public ServerType Type => ServerType.VANILLA;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(Manifest, ct).ConfigureAwait(false));
        var outList = new List<string>();
        foreach (var v in doc.RootElement.Prop("versions").Items())
        {
            if (v.Prop("type").AsText() != "release") continue;
            outList.Add(v.Prop("id").AsText() ?? "");
        }
        outList.Sort(VersionUtil.Descending);
        return outList;
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
