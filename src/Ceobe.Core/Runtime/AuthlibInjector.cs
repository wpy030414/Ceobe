using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Runtime;

/// <summary>
/// authlib-injector：以 <c>-javaagent</c> 方式注入，实现 Yggdrasil（三方鉴权）对接。
/// 语义照搬 Java 版 <c>runtime/AuthlibInjector</c>：缓存命中直接返回，否则查 latest.json 下载并校验 sha256。
/// </summary>
public static class AuthlibInjector
{
    private const string Latest = "https://authlib-injector.yushi.moe/artifact/latest.json";

    /// <summary>确保 authlib-injector.jar 已在缓存中，返回其绝对路径。</summary>
    public static async Task<string> EnsureAsync(IProgressSink? progress = null, CancellationToken ct = default)
    {
        var cached = System.IO.Path.Combine(
            CeobeHome.Ensure(System.IO.Path.Combine(CeobeHome.Root(), "cache", "jars")),
            "authlib-injector.jar");
        if (File.Exists(cached)) return cached;

        string? url;
        string? sha256;
        using (var doc = JsonUtil.Parse(await HttpUtil.GetStringAsync(Latest, ct).ConfigureAwait(false)))
        {
            url = GetStr(doc, "download_url");
            sha256 = doc.RootElement.TryGetProperty("checksums", out var cs)
                     && cs.ValueKind == System.Text.Json.JsonValueKind.Object
                ? GetStr(doc, "checksums", "sha256")
                : null;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new CeobeException("无法解析 authlib-injector 下载地址: " + Latest);
        }

        (progress ?? NullProgress.Instance).Update("下载 authlib-injector", 0, -1);
        await HttpUtil.DownloadAsync(url, cached, null, sha256, null, ct).ConfigureAwait(false);
        return cached;
    }

    private static string? GetStr(System.Text.Json.JsonDocument doc, params string[] path)
    {
        var el = doc.RootElement;
        foreach (var p in path)
        {
            if (!el.TryGetProperty(p, out var next)) return null;
            el = next;
        }
        return el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;
    }
}
