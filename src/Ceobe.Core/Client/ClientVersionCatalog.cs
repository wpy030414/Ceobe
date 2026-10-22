using Ceobe.Core.Providers;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>一个可供新建实例选择的游戏版本。</summary>
public sealed record VersionChoice(string Id, DateTimeOffset? Released);

/// <summary>
/// 版本与加载器清单。
///
/// 原版清单直接复用 <see cref="MojangProvider.ListReleasesAsync"/>：它已经处理了
/// 「BMCLAPI 只给 2 秒预算、超时回退直连、进程内缓存 30 分钟」这套踩过坑的逻辑，
/// 客户端再写一份必然漂移。
/// </summary>
public static class ClientVersionCatalog
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/versions";

    /// <summary>正式版列表（按版本号降序）。</summary>
    public static async Task<IReadOnlyList<VersionChoice>> ReleasesAsync(CancellationToken ct = default)
    {
        var releases = await MojangProvider.ListReleasesAsync(ct).ConfigureAwait(false);
        return releases.Select(r => new VersionChoice(r.Id, r.ReleaseTime)).ToList();
    }

    /// <summary>
    /// 某版本可用的加载器版本（降序，稳定版优先）。
    /// 网络失败抛 <see cref="CeobeException"/>，由界面展示为「无法获取」并允许重试——
    /// 静默返回空列表会让用户以为「这个版本不支持 Fabric」。
    /// </summary>
    public static async Task<IReadOnlyList<string>> LoaderVersionsAsync(
        ClientLoader loader, string gameVersion, CancellationToken ct = default)
    {
        if (loader != ClientLoader.FABRIC) return [];

        var json = await HttpUtil.GetStringAsync($"{FabricMeta}/loader/{gameVersion}", ct).ConfigureAwait(false);
        using var doc = JsonUtil.Parse(json);
        var rows = new List<(string Version, bool Stable)>();
        foreach (var item in doc.RootElement.Items())
        {
            var l = item.Prop("loader");
            var version = l.Prop("version").AsText();
            if (string.IsNullOrWhiteSpace(version)) continue;
            rows.Add((version!, l.Prop("stable").AsBool()));
        }
        // 稳定版在前；同组内按版本号降序（Fabric 的 loader 版本是语义化版本，可以比较）
        return rows
            .OrderByDescending(r => r.Stable)
            .ThenByDescending(r => r.Version, VersionComparer.Instance)
            .Select(r => r.Version)
            .ToList();
    }

    /// <summary>Fabric 的 profile JSON（含 mainClass 与加载器自身的库列表）。</summary>
    internal static Task<string> FabricProfileJsonAsync(string gameVersion, string loaderVersion,
        CancellationToken ct) =>
        HttpUtil.GetStringAsync($"{FabricMeta}/loader/{gameVersion}/{loaderVersion}/profile/json", ct);

    /// <summary>
    /// 加载器支持矩阵。未接入的加载器必须**明确说不支持**，而不是装到一半才失败：
    /// NeoForge/Forge 要跑官方 installer 改写版本目录，属于独立工作量。
    /// </summary>
    public static (bool Ok, string Reason) Support(ClientLoader loader) => loader switch
    {
        ClientLoader.VANILLA => (true, ""),
        ClientLoader.FABRIC => (true, ""),
        ClientLoader.QUILT => (false, "Quilt 的 profile 与 Fabric 不同构，尚未接入"),
        ClientLoader.NEOFORGE => (false, "需要运行官方 installer，施工中"),
        ClientLoader.FORGE => (false, "需要运行官方 installer，施工中"),
        _ => (false, "未知加载器"),
    };

    /// <summary>界面文案。</summary>
    public static string Label(this ClientLoader loader) => loader switch
    {
        ClientLoader.VANILLA => "原版",
        ClientLoader.FABRIC => "Fabric",
        ClientLoader.QUILT => "Quilt",
        ClientLoader.NEOFORGE => "NeoForge",
        ClientLoader.FORGE => "Forge",
        _ => loader.ToString(),
    };

    /// <summary>语义化版本比较（1.10 &gt; 1.9，而不是字符串序）。</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var a = x.Split('.', '-', '+');
            var b = y.Split('.', '-', '+');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                if (i >= a.Length) return -1;
                if (i >= b.Length) return 1;
                var na = int.TryParse(a[i], out var va);
                var nb = int.TryParse(b[i], out var vb);
                if (na && nb)
                {
                    if (va != vb) return va.CompareTo(vb);
                }
                else
                {
                    var c = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
            }
            return 0;
        }
    }
}
