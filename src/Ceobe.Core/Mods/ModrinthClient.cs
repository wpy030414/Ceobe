using System.Globalization;
using System.Text;
using System.Text.Json;
using Ceobe.Core.Model;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Mods;

/// <summary>
/// Modrinth API v2 客户端：搜索 / 版本 / 安装（服务端侧过滤）+ 整合包检索。
/// 逐字段对齐 Java 版 <c>mods/ModrinthClient.java</c>（docs/specs/module-mods.md）。
/// 请求一律走 <see cref="HttpUtil"/>（统一注入 User-Agent，Modrinth 硬要求）。
///
/// <see href="https://docs.modrinth.com/api/">Modrinth API 文档</see>
/// </summary>
public sealed class ModrinthClient
{
    private const string Base = "https://api.modrinth.com/v2";

    /// <summary>搜索结果条目（对齐 Java record SearchHit）。</summary>
    public sealed record SearchHit(string ProjectId, string Slug, string Title, string Description,
        string Author, long Downloads, IReadOnlyList<string> Categories, string? IconUrl);

    /// <summary>整合包搜索结果条目。</summary>
    public sealed record PackHit(string ProjectId, string Slug, string Title, string Description,
        string Author, long Downloads, IReadOnlyList<string> Categories);

    /// <summary>整合包某版本（含 .mrpack 文件）。</summary>
    public sealed record PackVersion(string ProjectId, string Slug, string Name, string VersionNumber,
        IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders,
        string Url, string? Sha1, long Size);

    /// <summary>
    /// 搜索模组。facets 过滤 <c>project_type:mod</c> + <c>versions:&lt;gv&gt;</c> + <c>categories:&lt;loader&gt;</c>，
    /// URL 组装顺序与 Java 版逐字符一致。
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> Search(string query, string? gameVersion,
        ServerType type, int limit, CancellationToken ct = default)
    {
        var loader = LoaderOf(type);
        var url = new StringBuilder(Base + "/search?limit=" + limit
            + "&query=" + Enc(query ?? ""));
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            url.Append("&facets=").Append(Enc(
                "[[\"project_type:mod\"],[\"versions:" + gameVersion + "\"],[\"categories:" + loader + "\"]]"));
        }
        else
        {
            url.Append("&facets=").Append(Enc(
                "[[\"project_type:mod\"],[\"categories:" + loader + "\"]]"));
        }

        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(url.ToString(), ct).ConfigureAwait(false));
        var hits = new List<SearchHit>();
        foreach (var h in doc.RootElement.Child("hits").Items())
        {
            hits.Add(new SearchHit(
                h.Child("project_id").Text(),
                h.Child("slug").Text(),
                h.Child("title").Text(),
                h.Child("description").Text(),
                h.Child("author").Text(),
                h.Child("downloads").Int(0),
                h.Child("display_categories").Strings(),
                h.Child("icon_url").OptText()));
        }
        return hits;
    }

    /// <summary>安装到实例 mods/，返回记录（对齐 Java install 的下载/校验/环境判定顺序）。</summary>
    public async Task<ModEntry> Install(string instanceDir, ServerType type, string projectIdOrSlug,
        string gameVersion, IProgressSink? progress = null, CancellationToken ct = default)
    {
        var sink = progress ?? NullProgress.Instance;
        if (!type.HasModsDir())
        {
            throw CeobeException.Of("{0} 没有 mods 目录，无法安装模组", type.ZhLabel());
        }

        var loader = LoaderOf(type);
        var url = Base + "/project/" + Enc(projectIdOrSlug) + "/version"
            + "?game_versions=" + Enc("[\"" + gameVersion + "\"]")
            + "&loaders=" + Enc("[\"" + loader + "\"]");
        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(url, ct).ConfigureAwait(false));
        var versions = doc.RootElement;
        if (versions.ValueKind != JsonValueKind.Array || !versions.EnumerateArray().Any())
        {
            throw CeobeException.Of("未找到适配 {0} + {1} 的版本", gameVersion, loader);
        }

        var version = PickPreferred(versions);
        var file = PrimaryFile(version);
        if (file is null) throw new CeobeException("该版本无可下载文件");

        // version.environment 是字符串枚举（如 client_only / client_or_server_prefers_both），
        // 不是 {client,server} 对象；仅 client_only 表示无法用于服务端（Java 侧回归点）。
        var environment = version.Child("environment").Text("unknown");
        if (ClientOnly(environment))
        {
            throw CeobeException.Of("「{0}」是客户端专属模组，不能装到服务端",
                version.Child("name").Text(projectIdOrSlug));
        }
        var envServer = "optional";
        var envClient = environment == "server_only" ? "unsupported" : "optional";

        var fileName = Path.GetFileName(file.Value.Child("filename").Text(""));
        if (fileName.Length == 0) throw new CeobeException("该版本文件名无效");
        var downloadUrl = file.Value.Child("url").Text("");
        var sha1 = file.Value.Child("hashes").Child("sha1").OptText();
        var modsDir = CeobeHome.Ensure(Path.Combine(instanceDir, "mods"));
        var target = Path.Combine(modsDir, fileName);

        var stage = "下载模组 " + fileName;
        sink.Update(stage, 0, file.Value.Child("size").Int(-1));
        await HttpUtil.DownloadAsync(downloadUrl, target, sha1, null,
            new ProgressSinkAdapter(stage, sink), ct).ConfigureAwait(false);

        return new ModEntry
        {
            ProjectId = version.Child("project_id").Text(projectIdOrSlug),
            Slug = projectIdOrSlug,
            Title = version.Child("name").Text(projectIdOrSlug),
            VersionNumber = version.Child("version_number").Text(),
            FileName = fileName,
            Sha1 = sha1 ?? "",
            EnvServer = envServer,
            EnvClient = envClient,
        };
    }

    /// <summary>卸载：删除记录中的文件（文件不存在视同成功，对齐 deleteIfExists）。</summary>
    public Task UninstallAsync(string instanceDir, ModEntry entry, CancellationToken ct = default)
    {
        try
        {
            File.Delete(Path.Combine(instanceDir, "mods", entry.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("删除模组文件失败: " + entry.FileName, e);
        }
        return Task.CompletedTask;
    }

    // —— 整合包 ——

    /// <summary>搜索整合包。facets 组装顺序与 Java 版一致（project_type → versions → categories）。</summary>
    public async Task<IReadOnlyList<PackHit>> SearchPacks(string query, string? gameVersion,
        string? loader, int limit, CancellationToken ct = default)
    {
        var facets = new StringBuilder("[[\"project_type:modpack\"]");
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            facets.Append(",[\"versions:").Append(gameVersion).Append("\"]");
        }
        if (!string.IsNullOrWhiteSpace(loader))
        {
            facets.Append(",[\"categories:").Append(loader).Append("\"]");
        }
        facets.Append("]");
        var url = Base + "/search?limit=" + limit + "&query=" + Enc(query ?? "")
            + "&facets=" + Enc(facets.ToString());

        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(url, ct).ConfigureAwait(false));
        var hits = new List<PackHit>();
        foreach (var h in doc.RootElement.Child("hits").Items())
        {
            hits.Add(new PackHit(
                h.Child("project_id").Text(),
                h.Child("slug").Text(),
                h.Child("title").Text(),
                h.Child("description").Text(),
                h.Child("author").Text(),
                h.Child("downloads").Int(0),
                h.Child("display_categories").Strings()));
        }
        return hits;
    }

    /// <summary>解析整合包版本（可选按 MC 版本 / 加载器过滤）。查询参数顺序对齐 Java（loaders 在前）。</summary>
    public async Task<PackVersion> ResolvePack(string projectIdOrSlug, string? gameVersion,
        string? loader, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(loader)) q.Add("loaders=" + Enc("[\"" + loader + "\"]"));
        if (!string.IsNullOrWhiteSpace(gameVersion)) q.Add("game_versions=" + Enc("[\"" + gameVersion + "\"]"));
        var url = Base + "/project/" + Enc(projectIdOrSlug) + "/version"
            + (q.Count == 0 ? "" : "?" + string.Join("&", q));

        using var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(url, ct).ConfigureAwait(false));
        var versions = doc.RootElement;
        if (versions.ValueKind != JsonValueKind.Array || !versions.EnumerateArray().Any())
        {
            throw CeobeException.Of("未找到整合包 {0} 的适配版本", projectIdOrSlug);
        }
        var v = PickPreferred(versions);
        var file = PrimaryFile(v);
        if (file is null) throw new CeobeException("该整合包版本无可下载文件");
        return new PackVersion(
            v.Child("project_id").Text(projectIdOrSlug),
            projectIdOrSlug,
            v.Child("name").Text(projectIdOrSlug),
            v.Child("version_number").Text(),
            v.Child("game_versions").Strings(),
            v.Child("loaders").Strings(),
            file.Value.Child("url").Text(""),
            file.Value.Child("hashes").Child("sha1").OptText(),
            file.Value.Child("size").Int(-1));
    }

    private static JsonElement PickPreferred(JsonElement versions)
    {
        // Modrinth 版本接口按发布时间降序返回 → 首个 release 即最新正式版
        foreach (var v in versions.EnumerateArray())
        {
            if (v.Child("version_type").Text() == "release") return v;
        }
        return versions.EnumerateArray().First();
    }

    private static JsonElement? PrimaryFile(JsonElement version)
    {
        var files = version.Child("files");
        JsonElement first = default;
        var has = false;
        foreach (var f in files.Items())
        {
            if (!has) { first = f; has = true; }
            if (f.Child("primary").Flag(false)) return f;
        }
        return has ? first : null;
    }

    /// <summary>
    /// 该 <c>version.environment</c> 字符串是否表示客户端专属（无法用于服务端）。
    /// 公开：GUI 的导入确认页也要用它解释拦截原因（Java 侧为包内可见 + 单测直调）。
    /// </summary>
    public static bool ClientOnly(string environment) => environment == "client_only";

    /// <summary>
    /// Modrinth 检索 facets 用的 categories 值。逐分支照搬 Java <c>loaderOf</c>：
    /// fabric→fabric、neoforge→neoforge、paper/purpur→paper、vanilla→datapack。
    /// （注意：这与地基 <c>ServerTypeInfo.LoaderId()</c>（仅 fabric/neoforge，其余 null）是两套语义，
    /// LoaderId 服务「模组安装可行性」判定，这里服务「检索过滤」，不能混用。）
    /// </summary>
    private static string LoaderOf(ServerType type) => type switch
    {
        ServerType.FABRIC => "fabric",
        ServerType.NEOFORGE => "neoforge",
        ServerType.PAPER or ServerType.PURPUR => "paper",
        ServerType.VANILLA => "datapack",
        _ => "fabric",
    };

    private static string Enc(string s) => Uri.EscapeDataString(s);
}

/// <summary>
/// JsonElement 的 Jackson <c>path()/asText()/asLong()</c> 风格导航（缺失/类型不符 → 兜底值，
/// 对齐 Java 侧「读不到就用默认值、不抛」的宽容读取）。仅本程序集可见。
/// </summary>
internal static class JsonNav
{
    public static JsonElement Child(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string Text(this JsonElement e, string fallback = "") =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : fallback;

    /// <summary>asText(null)：非字符串一律 null。</summary>
    public static string? OptText(this JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static long Int(this JsonElement e, long fallback) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : fallback;

    public static bool Flag(this JsonElement e, bool fallback) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => fallback,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    /// <summary>字符串数组 → List&lt;string&gt;；非字符串元素跳过（Java asText 会强转，类别字段恒为字符串，差异不影响语义）。</summary>
    public static IReadOnlyList<string> Strings(this JsonElement e)
    {
        var list = new List<string>();
        foreach (var i in e.Items())
        {
            if (i.ValueKind == JsonValueKind.String) list.Add(i.GetString()!);
        }
        return list;
    }

    public static JsonElement ParseRoot(string text) => JsonUtil.Parse(text).RootElement;
}
