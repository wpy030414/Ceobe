using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ceobe.Core.Providers;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// 客户端文件装配：版本 JSON、client.jar、libraries、assets、natives，以及加载器 profile。
///
/// 与「下载一个 jar 就能跑」的服务端不同，客户端要凑齐约 400 MB / 3000 个小文件。
/// 因此这里的三条硬规则：
/// <list type="number">
///   <item>**共享缓存**：libraries / assets / 版本文件按版本 id 落在 <c>cache/client</c>，实例只独占 natives；</item>
///   <item>**可中断可续**：已存在且非空的文件直接跳过，用户断网重来不必从头下；</item>
///   <item>**进度要能说出阶段**：界面上「正在下载 assets（1820/3041）」比一个转圈有用得多。</item>
/// </list>
///
/// 镜像改写由 <see cref="HttpUtil"/> → <see cref="Mirrors"/> 统一处理（Mojang → BMCLAPI，
/// Fabric meta → BMCLAPI/fabric-meta），本类不自己拼镜像地址。
/// </summary>
public static class ClientInstaller
{
    /// <summary>并发下载数。太高会被镜像限速，太低则 3000 个首包文件要下很久。</summary>
    private const int Parallelism = 6;

    /// <summary>首次安装（新建实例时调用）。可重复执行——第二次就是「修复游戏文件」。</summary>
    public static async Task InstallAsync(ClientMeta meta, IProgressSink? progress = null,
        CancellationToken ct = default)
    {
        var sink = progress ?? NullProgress.Instance;
        if (!ClientVersionCatalog.Support(meta.Loader).Ok)
        {
            throw CeobeException.Of("{0} 暂未支持：{1}", meta.Loader.Label(),
                ClientVersionCatalog.Support(meta.Loader).Reason);
        }

        // 1) 原版版本 JSON —— 落盘保存，启动时不再联网
        sink.Update($"读取 {meta.GameVersion} 版本清单", 0, -1);
        JsonDocument doc;
        try
        {
            doc = await MojangProvider.VersionJsonAsync(meta.GameVersion, ct).ConfigureAwait(false);
        }
        catch (CeobeException e)
        {
            throw new CeobeException($"无法获取 {meta.GameVersion} 的版本清单：{e.Message}", e);
        }

        string rawJson;
        using (doc)
        {
            rawJson = doc.RootElement.GetRawText();
            WriteText(ClientVersion.VersionJson(meta.GameVersion), rawJson);
        }

        using var versionDoc = JsonUtil.Parse(rawJson);
        var root = versionDoc.RootElement;

        // 2) client.jar
        var client = root.Prop("downloads").Prop("client");
        var clientUrl = client.Prop("url").AsText();
        if (!string.IsNullOrEmpty(clientUrl))
        {
            await DownloadFileAsync(clientUrl!, ClientVersion.ClientJar(meta.GameVersion),
                client.Prop("sha1").AsText(), sink, $"下载客户端 {meta.GameVersion}.jar", ct)
                .ConfigureAwait(false);
        }

        // 3) libraries
        var libraries = ClientVersion.ParseLibraries(root);
        await EnsureLibrariesAsync(libraries, sink, ct).ConfigureAwait(false);

        // 4) natives（旧版本才把 natives 独立列出来；1.19+ 走 lwjgl 的 natives classifier）
        ExtractNatives(meta, libraries, ClientVersion.ParseJvmArgs(root), sink, ct);

        // 5) assets
        await EnsureAssetsAsync(root, sink, ct).ConfigureAwait(false);

        // 6) 加载器 profile
        if (meta.Loader == ClientLoader.FABRIC)
        {
            await InstallFabricAsync(meta, sink, ct).ConfigureAwait(false);
        }

        sink.Update("安装完成", 1, 1);
    }

    // —— 库 ——

    private static async Task EnsureLibrariesAsync(IReadOnlyList<LibrarySpec> libraries,
        IProgressSink sink, CancellationToken ct)
    {
        var pending = new List<LibrarySpec>();
        foreach (var lib in libraries)
        {
            if (lib.Url is null) continue;
            var target = Path.Combine(ClientStore.LibrariesDir(),
                lib.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && new FileInfo(target).Length > 0) continue;
            pending.Add(lib);
        }

        if (pending.Count == 0)
        {
            sink.Update("库文件已齐全", 0, 0);
            return;
        }

        var done = 0;
        sink.Update($"下载库文件（0/{pending.Count}）", 0, pending.Count);
        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = Parallelism,
            CancellationToken = ct,
        }, async (lib, token) =>
        {
            var target = Path.Combine(ClientStore.LibrariesDir(),
                lib.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                await HttpUtil.DownloadAsync(lib.Url!, target, lib.Sha1, null, null, token)
                    .ConfigureAwait(false);
            }
            catch (CeobeException e) when (!token.IsCancellationRequested)
            {
                throw new CeobeException($"下载库 {lib.Name} 失败：{e.Message}", e);
            }
            var n = Interlocked.Increment(ref done);
            sink.Update($"下载库文件（{n}/{pending.Count}）", n, pending.Count);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// natives 解压。目标目录由版本 JSON 决定：老版本拍平到 natives 根目录，
    /// 26.3 起是 <c>natives/java</c> 这类子目录（见 jvm 参数里的 java.library.path）。
    /// </summary>
    private static void ExtractNatives(ClientMeta meta, IReadOnlyList<LibrarySpec> libraries,
        IReadOnlyList<string> jvmArgs, IProgressSink sink, CancellationToken ct)
    {
        var root = ClientStore.NativesDir(meta.Name);
        foreach (var sub in ClientVersion.NativeSubdirs(jvmArgs))
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }
        var subdir = ClientVersion.NativeLibrarySubdir(jvmArgs);
        var targetDir = subdir is null ? root : Path.Combine(root, subdir);
        Directory.CreateDirectory(targetDir);
        ExtractNativeJars(libraries, targetDir, sink, ct);
    }

    /// <summary>
    /// 启动前自愈：按版本声明的布局确认 natives 就位，缺失时从库 jar 里补解压。
    /// 这样修复前装好的实例（natives 被拍平）不必用户手动「修复游戏文件」。
    /// </summary>
    internal static void EnsureNatives(ClientMeta meta, ResolvedVersion version)
    {
        var root = ClientStore.NativesDir(meta.Name);
        foreach (var sub in ClientVersion.NativeSubdirs(version.JvmArgs))
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }
        var subdir = ClientVersion.NativeLibrarySubdir(version.JvmArgs);
        var targetDir = subdir is null ? root : Path.Combine(root, subdir);
        Directory.CreateDirectory(targetDir);
        if (Directory.EnumerateFiles(targetDir, "*.dll").Any()) return;
        ExtractNativeJars(version.Libraries, targetDir, NullProgress.Instance, CancellationToken.None);
    }

    private static void ExtractNativeJars(IEnumerable<LibrarySpec> libraries, string targetDir,
        IProgressSink sink, CancellationToken ct)
    {
        var natives = libraries.Where(l => l.IsNative).ToList();
        if (natives.Count == 0) return;

        sink.Update("解压 natives", 0, natives.Count);
        var done = 0;
        foreach (var lib in natives)
        {
            ct.ThrowIfCancellationRequested();
            var jar = Path.Combine(ClientStore.LibrariesDir(),
                lib.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jar)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.Length == 0) continue;
                    if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    // 只解 native 二进制，避免把 jar 里的 class 也倒进 natives
                    if (!IsNativeBinary(name)) continue;
                    var dest = Path.Combine(targetDir, name);
                    if (File.Exists(dest) && new FileInfo(dest).Length == entry.Length) continue;
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                throw new CeobeException($"解压 natives 失败：{lib.Name}（{e.Message}）", e);
            }
            done++;
            sink.Update("解压 natives", done, natives.Count);
        }
    }

    private static bool IsNativeBinary(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".dll" or ".so" or ".dylib" or ".jnilib";
    }

    // —— 资源 ——

    private static async Task EnsureAssetsAsync(JsonElement root, IProgressSink sink, CancellationToken ct)
    {
        var index = root.Prop("assetIndex");
        var indexId = index.Prop("id").AsText();
        var indexUrl = index.Prop("url").AsText();
        if (string.IsNullOrEmpty(indexId) || string.IsNullOrEmpty(indexUrl))
        {
            sink.Update("该版本无需资源文件", 0, 0);
            return;
        }

        var indexPath = Path.Combine(ClientStore.AssetsDir(), "indexes", indexId + ".json");
        if (!File.Exists(indexPath) || new FileInfo(indexPath).Length == 0)
        {
            await DownloadFileAsync(indexUrl!, indexPath, index.Prop("sha1").AsText(),
                sink, "下载资源索引 " + indexId, ct).ConfigureAwait(false);
        }

        // 索引本身就是 JSON：objects 是 name → {hash, size}
        var objects = new List<(string Hash, long Size)>();
        try
        {
            using var indexDoc = JsonUtil.Parse(File.ReadAllText(indexPath));
            foreach (var obj in indexDoc.RootElement.Prop("objects").Properties())
            {
                var hash = obj.Value.Prop("hash").AsText();
                if (string.IsNullOrEmpty(hash)) continue;
                objects.Add((hash!, obj.Value.Prop("size").AsLong(0)));
            }
        }
        catch (Exception e) when (e is IOException or CeobeException)
        {
            throw new CeobeException($"资源索引损坏，请删除后重试：{indexPath}", e);
        }

        var objectsRoot = Path.Combine(ClientStore.AssetsDir(), "objects");
        var pending = objects.Where(o =>
        {
            var p = ObjectPath(objectsRoot, o.Hash);
            return !(File.Exists(p) && new FileInfo(p).Length > 0);
        }).ToList();

        if (pending.Count == 0)
        {
            sink.Update($"资源文件已齐全（{objects.Count} 个）", 0, 0);
            return;
        }

        var done = 0;
        sink.Update($"下载资源文件（0/{pending.Count}）", 0, pending.Count);
        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = Parallelism,
            CancellationToken = ct,
        }, async (obj, token) =>
        {
            var target = ObjectPath(objectsRoot, obj.Hash);
            var url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}";
            await HttpUtil.DownloadAsync(url, target, obj.Hash, null, null, token).ConfigureAwait(false);
            var n = Interlocked.Increment(ref done);
            // 每 16 个文件报一次进度：3000 次回调会把 UI 线程刷爆
            if (n % 16 == 0 || n == pending.Count)
            {
                sink.Update($"下载资源文件（{n}/{pending.Count}）", n, pending.Count);
            }
        }).ConfigureAwait(false);
        sink.Update($"资源文件就绪（{objects.Count} 个）", pending.Count, pending.Count);
    }

    private static string ObjectPath(string objectsRoot, string hash) =>
        Path.Combine(objectsRoot, hash[..2], hash);

    // —— 加载器 ——

    private static async Task InstallFabricAsync(ClientMeta meta, IProgressSink sink, CancellationToken ct)
    {
        var loaderVersion = meta.LoaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            // 未指定时取第一个稳定版：新建向导会传，这里兜住 CLI / 手工改元数据的情况
            var versions = await ClientVersionCatalog.LoaderVersionsAsync(ClientLoader.FABRIC,
                meta.GameVersion, ct).ConfigureAwait(false);
            loaderVersion = versions.FirstOrDefault()
                ?? throw CeobeException.Of("Fabric 没有为 {0} 提供加载器版本", meta.GameVersion);
            meta.LoaderVersion = loaderVersion;
        }

        sink.Update($"获取 Fabric {loaderVersion} profile", 0, -1);
        var profileText = await ClientVersionCatalog.FabricProfileJsonAsync(
            meta.GameVersion, loaderVersion, ct).ConfigureAwait(false);

        var loaderId = ClientVersion.LoaderVersionId(meta);
        var profilePath = ClientVersion.VersionJson(loaderId);
        WriteText(profilePath, profileText);

        using var doc = JsonUtil.Parse(profileText);
        var libs = ClientVersion.ParseLibraries(doc.RootElement);
        await EnsureLibrariesAsync(libs, sink, ct).ConfigureAwait(false);
    }

    // —— IO ——

    private static async Task DownloadFileAsync(string url, string target, string? sha1,
        IProgressSink sink, string stage, CancellationToken ct)
    {
        if (File.Exists(target) && new FileInfo(target).Length > 0 && sha1 is null) return;
        sink.Update(stage, 0, -1);
        try
        {
            await HttpUtil.DownloadAsync(url, target, sha1, null,
                new ProgressSinkAdapter(stage, sink), ct).ConfigureAwait(false);
        }
        catch (CeobeException e)
        {
            throw new CeobeException($"{stage} 失败：{e.Message}", e);
        }
    }

    private static void WriteText(string path, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"写入 {path} 失败", e);
        }
    }
}
