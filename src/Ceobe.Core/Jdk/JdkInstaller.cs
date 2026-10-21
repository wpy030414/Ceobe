using System.Diagnostics;
using System.Runtime.InteropServices;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Jdk;

/// <summary>
/// 按平台一键安装 Temurin JDK（Eclipse Adoptium）。
/// <para>
/// 优先清华镜像（<c>mirrors.tuna.tsinghua.edu.cn/Adoptium/&lt;major&gt;/jdk/&lt;arch&gt;/&lt;os&gt;/&lt;包名&gt;</c>），
/// 失败回退 Adoptium 官方 GitHub releases；两处下载都按 API 的 <c>package.checksum</c>（SHA-256）校验，
/// 防镜像内容滞后损坏（ADR-012「校验失败即回退上游」的强化版）。
/// </para>
/// <para>
/// 产物落在 {@code ~/.ceobe/jdk/<jdk-…>/}。失败可见化（ADR-018 后果 + 迁移新增要求）：任何一步失败，
/// 异常消息列出**已尝试的 URL 与原因**，并给出可执行下一步（{@code ceobe jdk install <n>} /
/// 手动指定 {@code --java-home}）。绝不允许静默失败。
/// </para>
/// </summary>
public static class JdkInstaller
{
    private const string AssetsApi =
        "https://api.adoptium.net/v3/assets/latest/{0}/hotspot?os={1}&architecture={2}&image_type=jdk";

    /// <summary>
    /// 清华 Adoptium 镜像目录布局（实测 2026-10-21 HEAD 200）：
    /// <c>Adoptium/{major}/jdk/{arch}/{os}/{package.name}</c>——arch 与 os 的顺序和 GitHub
    /// 包名内嵌的 <c>_x64_windows_</c> 一致，但**层级不同**（镜像按 major/jdk/arch/os 分目录），
    /// 所以必须由 API 返回的 package.name 拼接，不能直接换主机改写路径。
    /// </summary>
    private const string TsinghuaFormat =
        "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{0}/jdk/{1}/{2}/{3}";

    /// <summary>
    /// 安装指定主版本的 Temurin JDK，返回可用 javaHome（已含 bin/java[.exe]，ADR-018 校验通过）。
    /// </summary>
    /// <param name="major">Java 主版本（如 17/21/25）。</param>
    /// <param name="progress">阶段进度（对齐地基 <see cref="IProgressSink"/> 与 Java 版 ProgressListener），可空。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<string> InstallAsync(
        int major, IProgressSink? progress = null, CancellationToken ct = default)
    {
        var os = OsName();
        var arch = ArchName();

        // 1. 查 Adoptium assets API：拿到包名 + GitHub 直链 + SHA-256
        var apiUrl = string.Format(AssetsApi, major, os, arch);
        string name, ghLink, sha256;
        try
        {
            var json = await HttpUtil.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
            using var doc = JsonUtil.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array
                || doc.RootElement.GetArrayLength() == 0)
            {
                throw CeobeException.Of(
                    "Adoptium 无 Java {0} 的 {1}/{2} 构建（API: {3}）。" +
                    "请确认主版本号（常用 17 / 21 / 25），或稍后重试镜像同步。",
                    major, os, arch, apiUrl);
            }
            var pkg = doc.RootElement[0].GetProperty("binary").GetProperty("package");
            name = pkg.GetProperty("name").GetString() ?? "";
            ghLink = pkg.GetProperty("link").GetString() ?? "";
            sha256 = pkg.TryGetProperty("checksum", out var cs) ? cs.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ghLink))
            {
                throw CeobeException.Of(
                    "Adoptium 响应缺少 package 字段（API: {0}），上游契约可能已变更，请反馈", apiUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (CeobeException) { throw; }
        catch (Exception e)
        {
            throw CeobeException.Of(
                "查询 Adoptium JDK 元数据失败: {0}（原因: {1}）。\n下一步：检查网络后重试 `ceobe jdk install {2}`",
                apiUrl, e.Message, major);
        }

        // 2. 下载（已缓存则跳过）
        var cacheDir = Path.Combine(CeobeHome.Root(), "cache", "jdk");
        CeobeHome.Ensure(cacheDir);
        var archive = Path.Combine(cacheDir, name);
        var tunaUrl = string.Format(TsinghuaFormat, major, arch, os, name);

        if (!File.Exists(archive))
        {
            Report(progress, $"下载 JDK {major}（{os}/{arch}）", 0, -1);
            await DownloadWithFallbackAsync(tunaUrl, ghLink, archive, sha256, major, progress, ct)
                .ConfigureAwait(false);
        }
        else
        {
            Report(progress, $"复用缓存 JDK: {name}", 0, -1);
        }

        // 3. 解压（zip → ZipUtil 防目录穿越；tar.gz → 系统 tar）
        Report(progress, "解压 JDK", 0, -1);
        var home = await ExtractAsync(archive, ct).ConfigureAwait(false);

        // 4. ADR-018 守卫：解压产物必须确有 java 启动器才算装好
        if (!JavaEnv.HasLauncher(home))
        {
            throw CeobeException.Of(
                "解压后未找到含 bin/{0} 的 JDK 目录（归档可能损坏或布局异常）: {1}\n" +
                "下一步：删除缓存文件 {2} 后重试 `ceobe jdk install {3}`",
                JavaEnv.JavaExe, home, archive, major);
        }

        Report(progress, $"JDK 就绪: {home}", 1, 1);
        return home;
    }

    /// <summary>镜像优先（清华）、失败回退 GitHub；两处用同一 SHA-256 校验。全部失败给出双 URL 错误。</summary>
    private static async Task DownloadWithFallbackAsync(
        string tunaUrl, string ghUrl, string archive, string sha256, int major,
        IProgressSink? progress, CancellationToken ct)
    {
        // 地基 ProgressSinkAdapter：把 (done,total) 字节进度接到 IProgressSink 的 stage 文案
        var dl = progress is null ? null : new ProgressSinkAdapter($"下载 JDK {major}", progress);
        CeobeException? tunaEx = null;
        try
        {
            // sha256 为空时不校验（API 异常返回），非空时强校验防镜像滞后
            await HttpUtil.DownloadAsync(tunaUrl, archive, sha1: null,
                string.IsNullOrEmpty(sha256) ? null : sha256, dl, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) { throw; }
        catch (CeobeException e)
        {
            tunaEx = e;
        }
        catch (Exception e)
        {
            tunaEx = new CeobeException($"下载失败: {e.Message}", e);
        }

        Report(progress, $"清华镜像不可用（{tunaEx.Message}），改用 Adoptium 官方源", 0, -1);
        try
        {
            await HttpUtil.DownloadAsync(ghUrl, archive, sha1: null,
                string.IsNullOrEmpty(sha256) ? null : sha256, dl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception upstreamEx)
        {
            throw CeobeException.Of(
                "JDK {0} 下载失败：\n  清华镜像: {1} → {2}\n  GitHub 官方: {3} → {4}\n" +
                "下一步：检查网络；若持续失败，手动下载 {1} 放入 {5} 后重试，或用 --java-home 指定已有 JDK。",
                major, tunaUrl, tunaEx!.Message, ghUrl, upstreamEx.Message, archive);
        }
    }

    private static void Report(IProgressSink? progress, string msg, long done, long total) =>
        progress?.Update(msg, done, total);

    /// <summary>解压并把新出现的、含 bin/java 的目录作为 javaHome（对齐 Java 版 diff-before 策略，兼容顶层目录名不确定）。internal 供单测直接喂假归档。</summary>
    internal static async Task<string> ExtractAsync(string archive, CancellationToken ct)
    {
        var root = JdkStore.Root();
        var before = new HashSet<string>(ChildNames(root), StringComparer.Ordinal); // 目录名集合（与 Java 版一致）
        var fn = Path.GetFileName(archive).ToLowerInvariant();
        if (fn.EndsWith(".zip"))
        {
            // 解压数千文件耗时数秒，丢后台线程保持异步
            await Task.Run(() => ZipUtil.Extract(archive, root), ct).ConfigureAwait(false);
        }
        else if (fn.EndsWith(".tar.gz") || fn.EndsWith(".tgz"))
        {
            await UntarAsync(archive, root, ct).ConfigureAwait(false);
        }
        else
        {
            throw CeobeException.Of("不支持的 JDK 归档格式: {0}", fn);
        }

        foreach (var dir in ChildDirs(root))
        {
            ct.ThrowIfCancellationRequested();
            if (before.Contains(Path.GetFileName(dir))) continue;
            if (JavaEnv.HasLauncher(dir)) return dir;
        }
        throw CeobeException.Of(
            "解压后未出现新 JDK 目录（bin/{0} 缺失或顶层结构异常）: {1}\n" +
            "下一步：删除 {1} 重新下载，或检查清华镜像该路径下是否为完整包。",
            JavaEnv.JavaExe, archive);
    }

    /// <summary>列出 dir 下所有子项的**名称**（对齐 Java 版 childNames；读取失败返回空）。</summary>
    private static IEnumerable<string> ChildNames(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir)
                .Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> ChildDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task UntarAsync(string archive, string dest, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{archive}\" -C \"{dest}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) throw CeobeException.Of("无法启动 tar 进程");
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }))
            {
                var stdout = p.StandardOutput.ReadToEndAsync(ct);
                var stderr = p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                var combined = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
                if (p.ExitCode != 0)
                {
                    throw CeobeException.Of(
                        "tar 解压失败（退出码 {0}）: {1}\n下一步：确认系统 tar 可用（Windows 10 1803+ 自带）。",
                        p.ExitCode, combined.Trim());
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (CeobeException) { throw; }
        catch (Exception e)
        {
            throw CeobeException.Of(
                "无法调用 tar 解压（Linux/macOS 需系统 tar）: {0}\n下一步：安装 GNU tar 后重试。", e.Message);
        }
    }

    /// <summary>os ∈ {windows, linux, mac}（对齐 Java 版 JdkInstaller.os()，Adoptium API 用 mac）。</summary>
    internal static string OsName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "mac";
        return "linux";
    }

    /// <summary>arch ∈ {x64, aarch64}（对齐 Java 版 JdkInstaller.arch()）。</summary>
    internal static string ArchName() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
}
