using System.Text.Json;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Mods;

/// <summary>
/// Modrinth 整合包（<c>.mrpack</c>，实为 zip）安装器。
/// 逐条对齐 Java 版 <c>mods/MrpackInstaller.java</c> 与 docs/MIGRATION.md 硬契约 6：
/// 读取 <c>modrinth.index.json</c>：按 <c>files[]</c> 下载（跳过 <c>env.server=unsupported</c>），
/// 再解 <c>server-overrides/</c>（优先）与 <c>overrides/</c> 覆盖文件。
///
/// 整改项 4 扩展：<see cref="InstallFromLocalAsync"/> 让本地 .mrpack 复用同一套解析/安装逻辑
/// （<see cref="ReadIndex"/> 与 <see cref="InstallAsync"/> 的对外语义不变，本地路径天然被它们接受）。
/// </summary>
public static class MrpackInstaller
{
    /// <summary>整合包索引摘要（仅头部信息）。</summary>
    public sealed record Index(string Name, string VersionId, string? McVersion,
        string? Loader, string? LoaderVersion);

    /// <summary>安装结果。</summary>
    public sealed record Result(string Name, string VersionId, int Files, int Skipped, int Overrides);

    /// <summary>
    /// 下载整合包 .mrpack 到缓存目录（cache/packs），返回本地路径；已存在则复用（对齐 Java fetch）。
    /// </summary>
    public static async Task<string> FetchAsync(ModrinthClient.PackVersion pv,
        IProgressSink? progress = null, CancellationToken ct = default)
    {
        var cache = CeobeHome.Ensure(Path.Combine(CeobeHome.Root(), "cache", "packs"));
        var safeVersion = Sanitize(pv.VersionNumber);
        var fileName = pv.Slug + "-" + safeVersion + ".mrpack";
        var target = Path.Combine(cache, fileName);
        if (!File.Exists(target))
        {
            await HttpUtil.DownloadAsync(pv.Url, target, pv.Sha1, null,
                new ProgressSinkAdapter("下载整合包 " + fileName, progress ?? NullProgress.Instance), ct)
                .ConfigureAwait(false);
        }
        return target;
    }

    /// <summary>
    /// 解析 <c>modrinth.index.json</c>（仅读头部信息，不解包到实例）。
    /// 语义对齐 Java readIndex：缺索引文件抛业务异常；临时目录命名 <c>&lt;包名&gt;.index</c>、用后即删。
    /// </summary>
    public static Index ReadIndex(string mrpack)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(mrpack))!;
        var tmp = CeobeHome.Ensure(Path.Combine(dir, Path.GetFileName(mrpack) + ".index"));
        try
        {
            ZipUtil.DeleteRecursively(tmp);
            ZipUtil.Extract(mrpack, tmp);
            var index = Path.Combine(tmp, "modrinth.index.json");
            if (!File.Exists(index))
            {
                throw new CeobeException("不是有效的 .mrpack：缺少 modrinth.index.json");
            }
            using var doc = JsonUtil.Parse(File.ReadAllText(index));
            var root = doc.RootElement;
            var deps = root.Child("dependencies");
            string? loader = null;
            string? loaderVersion = null;
            // 依赖键 → 归一化 loader 名（顺序对齐 Java 的 Map.of 迭代：不保证顺序但键互斥）
            foreach (var (key, name) in LoaderDeps)
            {
                var v = deps.Child(key);
                if (v.ValueKind == JsonValueKind.Undefined) continue;
                loader = name;
                loaderVersion = v.OptText();
                break;
            }
            return new Index(
                root.Child("name").Text(""),
                root.Child("versionId").Text(""),
                deps.Child("minecraft").OptText(),
                loader, loaderVersion);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("读取整合包索引失败: " + e.Message, e);
        }
        finally
        {
            ZipUtil.DeleteRecursively(tmp);
        }
    }

    /// <summary>
    /// 安装整合包：兼容两种形态——直接是 .mrpack，或外层归档（zip 内含唯一 .mrpack，
    /// 整改项 4 的 ModImporter 会用外层 zip 路径调到这里）。
    /// </summary>
    public static Task<Result> InstallAsync(string instanceDir, string mrpackOrZip,
        IProgressSink? progress = null, CancellationToken ct = default) =>
        InstallFromLocalAsync(instanceDir, mrpackOrZip, progress, ct);

    /// <summary>
    /// 整改项 4 地基：从**本地文件**安装（与远程下载走同一 <see cref="ReadIndex"/>/<see cref="InstallCoreAsync"/>）。
    /// 既接受 .mrpack，也接受「解一层就是 .mrpack」的 zip（ModImporter 识别后的入口）。
    /// </summary>
    public static async Task<Result> InstallFromLocalAsync(string instanceDir, string localFile,
        IProgressSink? progress = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(localFile);
        if (!File.Exists(full))
        {
            throw CeobeException.Of("整合包文件不存在: {0}", localFile);
        }

        // .mrpack 本质是 zip：优先按 mrpack 直装；若缺索引且能从 zip 里挖出唯一 .mrpack，则解包再装
        string? unwrapped = null;
        try
        {
            return await InstallCoreAsync(instanceDir, full, progress, ct).ConfigureAwait(false);
        }
        catch (CeobeException e) when (IsMissingIndex(e))
        {
            unwrapped = UnwrapPack(full);
            if (unwrapped is null) throw;
            return await InstallCoreAsync(instanceDir, unwrapped, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (unwrapped is not null)
            {
                try { File.Delete(unwrapped); } catch (IOException) { }
            }
        }
    }

    /// <summary>把外层归档里唯一的 .mrpack 解到临时目录，返回其路径；没有或歧义则 null。</summary>
    private static string? UnwrapPack(string outerZip)
    {
        var tmp = CeobeHome.Ensure(outerZip + ".unpack");
        try
        {
            ZipUtil.DeleteRecursively(tmp);
            ZipUtil.Extract(outerZip, tmp);
            var candidates = Directory.GetFiles(tmp, "*.mrpack", SearchOption.AllDirectories);
            if (candidates.Length != 1) return null;   // 歧义交给调用方的原始异常，不静默猜
            var target = Path.Combine(
                Path.GetDirectoryName(outerZip)!,
                Path.GetFileNameWithoutExtension(outerZip) + ".inner.mrpack");
            File.Move(candidates[0], target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ZipUtil.DeleteRecursively(tmp);
        }
    }

    private static bool IsMissingIndex(CeobeException e) => e.Message.Contains("modrinth.index.json");

    private static async Task<Result> InstallCoreAsync(string instanceDir, string mrpack,
        IProgressSink? progress, CancellationToken ct)
    {
        var index = ReadIndex(mrpack);
        var sink = progress ?? NullProgress.Instance;

        var fullInstance = Path.GetFullPath(instanceDir);
        var work = CeobeHome.Ensure(Path.Combine(fullInstance, ".mrpack-work"));
        ZipUtil.DeleteRecursively(work);
        try
        {
            ZipUtil.Extract(mrpack, work);
            using var doc = JsonUtil.Parse(File.ReadAllText(Path.Combine(work, "modrinth.index.json")));
            var root = doc.RootElement;

            var files = 0;
            var skipped = 0;
            foreach (var f in root.Child("files").Items())
            {
                var envServer = f.Child("env").Child("server").Text("required");
                var relPath = f.Child("path").Text("");
                var url = FirstDownload(f);
                if (envServer == "unsupported" || relPath.Length == 0 || url is null)
                {
                    skipped++;
                    continue;
                }
                var target = Path.GetFullPath(Path.Combine(fullInstance, relPath));
                if (!IsWithin(target, fullInstance))   // 防穿越（对齐 Java normalize+startsWith）
                {
                    skipped++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var sha1 = f.Child("hashes").Child("sha1").OptText();
                sink.Update("下载整合包文件 " + Path.GetFileName(target), 0, -1);
                await HttpUtil.DownloadAsync(url, target, sha1, null, null, ct).ConfigureAwait(false);
                files++;
            }

            // server-overrides 优先于 overrides（AGENTS 硬契约 6）。
            // CopyInto 的覆盖语义是「后写者胜」，故先复制 overrides、后复制 server-overrides，
            // 同名冲突时 server-overrides 落地——这才符合契约 6 与 Java 版自身注释的「前者优先」。
            // Java 版按 `copyInto(server-overrides) + copyInto(overrides)` 的表达式顺序执行，同名文件
            // 实际是 overrides 胜出，与其注释宣称的优先级相矛盾，属隐性 bug；此处按契约语义修正
            // （计数不变，仍为两者文件数之和）。详见交付报告「语义差异」。
            var overrides = ZipUtil.CopyInto(Path.Combine(work, "overrides"), fullInstance)
                + ZipUtil.CopyInto(Path.Combine(work, "server-overrides"), fullInstance);

            return new Result(index.Name, index.VersionId, files, skipped, overrides);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("安装整合包失败: " + e.Message, e);
        }
        finally
        {
            ZipUtil.DeleteRecursively(work);
        }
    }

    private static string? FirstDownload(JsonElement file)
    {
        var downloads = file.Child("downloads");
        foreach (var d in downloads.Items())
        {
            return d.OptText();
        }
        return null;
    }

    private static readonly (string Key, string Name)[] LoaderDeps =
    [
        ("fabric-loader", "fabric"),
        ("neoforge", "neoforge"),
        ("forge", "forge"),
        ("quilt-loader", "quilt"),
    ];

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' ? c : '_');
        }
        return sb.ToString();
    }

    private static bool IsWithin(string candidate, string root)
    {
        var c = TrimSep(candidate);
        var r = TrimSep(root);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.Equals(r, cmp) || c.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }

    private static string TrimSep(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
