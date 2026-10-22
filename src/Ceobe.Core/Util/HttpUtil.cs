using System.Security.Cryptography;

namespace Ceobe.Core.Util;

/// <summary>
/// HTTP 封装：统一 User-Agent、超时、下载与校验（对齐 Java 版 <c>util/Http</c>）。
///
/// 架构约定：core 一律**异步**（GUI 不得阻塞 UI 线程，CLI 在 Main 里 await）。
/// Java 版是同步 + 虚拟线程，语义等价但调用风格不同，这是本次重构唯一允许的形态差异。
/// 所有对外请求必须带 User-Agent（Modrinth 要求，Fill v3 建议）——AGENTS 全局规则。
/// </summary>
public static class HttpUtil
{
    public const string UserAgent = "Ceobe/26.10 (https://github.com/wpy030414/Ceobe)";

    /// <summary>单次下载的空闲超时：超过此时长无新字节即中断（用于镜像挂起时回退）。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // 用 per-request CTS 取代整体超时
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>优先走镜像站，失败回退上游。mirrorTimeout 可给镜像一个更短的预算（元数据接口用）。</summary>
    public static async Task<string> GetStringAsync(
        string url, CancellationToken ct = default, TimeSpan? mirrorTimeout = null)
    {
        var mirror = Mirrors.ForUrl(url);
        if (mirror is not null)
        {
            try
            {
                var text = await GetDirectStringAsync(mirror, ct, mirrorTimeout ?? TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);
                // 镜像返回 200 但空体（BMCLAPI 的 manifest 曾实测如此）也算失败，回退上游。
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch (CeobeException) { /* 镜像不可用/超时 → 回退上游 */ }
        }
        return await GetDirectStringAsync(url, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    /// <summary>优先走镜像站，失败回退上游。</summary>
    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        var mirror = Mirrors.ForUrl(url);
        if (mirror is not null)
        {
            try { return await GetDirectBytesAsync(mirror, ct).ConfigureAwait(false); }
            catch (CeobeException) { /* 回退上游 */ }
        }
        return await GetDirectBytesAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载到 target（<c>.part</c> 原子改名），可选 sha1/sha256 校验；优先镜像站，失败回退上游。
    /// </summary>
    public static async Task DownloadAsync(
        string url, string target, string? sha1 = null, string? sha256 = null,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        var mirror = Mirrors.ForUrl(url);
        if (mirror is not null)
        {
            try
            {
                await DownloadDirectAsync(mirror, target, sha1, sha256, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (CeobeException) when (!ct.IsCancellationRequested)
            {
                // 镜像不可用 → 回退上游
            }
        }
        await DownloadDirectAsync(url, target, sha1, sha256, progress, ct).ConfigureAwait(false);
    }

    private static async Task<string> GetDirectStringAsync(string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw CeobeException.Of("请求失败 HTTP {0}: {1}", (int)resp.StatusCode, Trim(url));
            }
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (CeobeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new CeobeException($"网络请求失败: {Trim(url)} ({e.Message})", e);
        }
    }

    private static async Task<byte[]> GetDirectBytesAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw CeobeException.Of("下载失败 HTTP {0}: {1}", (int)resp.StatusCode, Trim(url));
            }
            return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (CeobeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new CeobeException($"下载失败: {Trim(url)} ({e.Message})", e);
        }
    }

    private static async Task DownloadDirectAsync(
        string url, string target, string? sha1, string? sha256,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var part = target + ".part";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target))!);
        try
        {
            using var resp = await Client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw CeobeException.Of("下载失败 HTTP {0}: {1}", (int)resp.StatusCode, Trim(url));
            }

            var total = resp.Content.Headers.ContentLength ?? -1L;
            var h1 = sha1 is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
            var h256 = sha256 is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

            await using (var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var outStream = new FileStream(part, FileMode.Create, FileAccess.Write,
                             FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long done = 0;
                while (true)
                {
                    // 空闲看门狗：镜像把流挂住时主动中断（触发上层回退上游），避免干等到总超时
                    using var idle = new CancellationTokenSource(IdleTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                    int n;
                    try
                    {
                        n = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw new CeobeException(
                            $"下载停滞（{IdleTimeout.TotalSeconds:0} 秒无数据）: {Trim(url)}");
                    }
                    if (n <= 0) break;

                    await outStream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    h1?.AppendData(buffer, 0, n);
                    h256?.AppendData(buffer, 0, n);
                    done += n;
                    progress?.Report((done, total));
                }
            }

            if (sha1 is not null)
            {
                Verify("SHA-1", target, sha1, h1?.GetHashAndReset(), ct);
            }
            if (sha256 is not null)
            {
                Verify("SHA-256", target, sha256, h256?.GetHashAndReset(), ct);
            }

            h1?.Dispose();
            h256?.Dispose();
            File.Move(part, target, overwrite: true);
        }
        catch (Exception e)
        {
            SafeDelete(part);
            if (e is CeobeException or OperationCanceledException) throw;
            throw new CeobeException($"下载中断: {Trim(url)} ({e.Message})", e);
        }
    }

    /// <summary>校验失败即抛；streamHash 为 null 时回读文件重算（对齐 Java 双路逻辑）。</summary>
    private static void Verify(string algo, string file, string expected, byte[]? streamHash, CancellationToken ct)
    {
        var actual = streamHash is not null
            ? Convert.ToHexString(streamHash).ToLowerInvariant()
            : HashFile(file, algo == "SHA-256" ? "SHA256" : "SHA1");
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw CeobeException.Of("{0} 校验失败\n期望: {1}\n实际: {2}", file, expected, actual);
        }
    }

    /// <summary>文件 SHA-1（hex 小写）。</summary>
    public static string Sha1(string file) => HashFile(file, "SHA1");

    public static string Sha256(string file) => HashFile(file, "SHA256");

    /// <summary>algo 取 "SHA1" / "SHA256" / "MD5"。</summary>
    public static string HashFile(string file, string algo)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            return HashStream(stream, algo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"计算哈希失败: {file}", e);
        }
    }

    private static string HashStream(Stream stream, string algo)
    {
        var h = IncrementalHash.CreateHash(Algorithm(algo));
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) h.AppendData(buffer, 0, n);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private static HashAlgorithmName Algorithm(string algo) => algo switch
    {
        "SHA1" => HashAlgorithmName.SHA1,
        "SHA256" => HashAlgorithmName.SHA256,
        "MD5" => HashAlgorithmName.MD5,
        _ => throw CeobeException.Of("不支持的哈希算法: {0}", algo),
    };

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Trim(string url) => url.Length > 96 ? url[..96] + "…" : url;
}
