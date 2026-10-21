using System.Diagnostics;
using System.Text;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// Providers 包内部共用设施：jar 去重缓存、文件复制、子进程执行、java 定位。
/// 对齐 Java 侧散落在 MojangProvider / FabricProvider / NeoForgeProvider 中的 static 辅助方法。
/// </summary>
internal static class ProviderSupport
{
    /// <summary>子进程输出保留上限（字符），与 Java 侧 20_000 一致。</summary>
    private const int OutputCap = 20_000;

    /// <summary>静默进度（对齐 Java <c>ProgressListener.SILENT</c>）。</summary>
    public static readonly IProgress<InstallProgress> Silent =
        new Progress<InstallProgress>(_ => { });

    public static void Report(IProgress<InstallProgress>? progress, string stage, long done, long total)
    {
        progress?.Report(new InstallProgress(stage, done, total));
    }

    /// <summary>
    /// 带 sha 去重的 jar 缓存：同名同 sha1 命中则直接复用（对齐 Java <c>MojangProvider.cachedJar</c>）。
    /// 注意：命中校验只看 sha1（sha256-only 的条目命中时不复核）——Java 版如此，照搬不改。
    /// </summary>
    public static async Task<string> CachedJarAsync(
        string key, string url, string? sha1, string? sha256,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var cached = Path.Combine(CeobeHome.JarCache(), key);
        if (File.Exists(cached))
        {
            var actual = sha1 is not null ? HttpUtil.Sha1(cached) : null;
            if (sha1 is null || sha1.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, "命中缓存 " + key, 1, 1);
                return cached;
            }
        }
        await HttpUtil.DownloadAsync(url, cached, sha1, sha256, new ByteProgress(progress, "下载 " + key), ct)
            .ConfigureAwait(false);
        return cached;
    }

    /// <summary>把 HttpUtil 的字节元组进度桥接为带 stage 的 InstallProgress。</summary>
    private sealed class ByteProgress(IProgress<InstallProgress>? target, string stage)
        : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) =>
            target?.Report(new InstallProgress(stage, value.Done, value.Total));
    }

    /// <summary>复制到实例目录（保父目录、覆写）。对齐 Java <c>MojangProvider.copy</c>。</summary>
    public static void Copy(string from, string to)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(to));
            if (parent is not null) Directory.CreateDirectory(parent);
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception e)
        {
            throw new CeobeException("复制到实例目录失败: " + to, e);
        }
    }

    /// <summary>
    /// 定位 java 可执行文件。语义差异说明：Java 版取 <c>java.home</c>（ceobe 进程自身即 JVM）；
    /// C# 进程无属性体系，改为 CEOBE_JAVA_HOME &gt; JAVA_HOME &gt; PATH 上的 "java"，
    /// 与 CeobeHome.Root 的「系统属性 → 环境变量」迁移规则一致。
    /// </summary>
    public static string JavaBinary()
    {
        var exe = "java" + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var home in new[]
                 {
                     Environment.GetEnvironmentVariable("CEOBE_JAVA_HOME"),
                     Environment.GetEnvironmentVariable("JAVA_HOME"),
                 })
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            var candidate = Path.Combine(home, "bin", exe);
            if (File.Exists(candidate)) return candidate;
        }
        return "java";
    }

    /// <summary>
    /// 运行子进程并合并 stdout/stderr（对齐 Java redirectErrorStream(true)）；
    /// 非零退出码抛错并附输出尾部，等价 Java runAndCapture / runInstaller。
    /// </summary>
    public static async Task RunCapturedAsync(
        string stageName, string program, IReadOnlyList<string> args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sb = new StringBuilder();
        var gate = new object();
        void Append(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                sb.Append(line).Append('\n');
                if (sb.Length > OutputCap) sb.Remove(0, sb.Length - OutputCap);
            }
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
            if (process is null) throw new CeobeException($"无法启动 {stageName}");
        }
        catch (Exception e) when (e is not CeobeException)
        {
            throw new CeobeException($"无法启动 {stageName}: {e.Message}", e);
        }

        using (process)
        {
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消 = 安装被中断：终结子进程树后继续上抛，对齐 Java 的中断语义
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* 进程已退出或无权终结 */ }
                throw;
            }
            var code = process.ExitCode;
            if (code != 0)
            {
                string snapshot;
                lock (gate) snapshot = sb.ToString();
                throw CeobeException.Of("{0} 退出码 {1}\n{2}", stageName, code, Tail(snapshot));
            }
        }
    }

    /// <summary>取文本末尾 12 行（对齐 Java tail：split 丢弃行尾空段）。</summary>
    public static string Tail(string text)
    {
        var lines = text.Split('\n');
        // Java split("\n") 会丢弃行尾连续空串，C# Split 保留，先对齐再取尾
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Length == 0) end--;
        lines = lines[..end];
        var from = Math.Max(0, lines.Length - 12);
        return string.Join('\n', lines[from..]);
    }
}
