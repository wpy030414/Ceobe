using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Ceobe.Core.Util;

namespace Ceobe.Core.Jdk;

/// <summary>
/// Java 运行时的探测与「可用性」判定——ADR-018 教训在 C# 版的守卫层。
///
/// ADR-018：一个「看起来是 JDK 却没有 <c>bin/java(.exe)</c> 的残破目录」绝不可用于 fork 服务端进程
/// （Java 版曾因 jpackage 剥离启动器而「启动实例静默失败」）。C# 版同样必须校验，入口即
/// <see cref="HasLauncher"/>；<see cref="HostJavaHome"/>、<see cref="JdkStore"/>、
/// <see cref="JavaResolver"/>、<see cref="JdkInstaller"/> 全部以它为准。
///
/// ADR-019 差异：C# 进程不是 JVM，Java 版「继承当前 JVM（java.home）」这一档在 C# 版**不存在**，
/// 本类不再有 runtimeHome()/runtimeMajor()，宿主来源只有 JAVA_HOME。
///
/// Windows 优先语义：启动器文件名按当前 OS 决定是否带 <c>.exe</c>；JAVA_HOME 经
/// <see cref="Environment.GetEnvironmentVariable(string)"/> 读取（Windows 上环境变量大小写不敏感）。
/// </summary>
public static partial class JavaEnv
{
    /// <summary>java 启动器文件名：Windows 为 <c>java.exe</c>，其余为 <c>java</c>（对齐 Java 版 JdkStore.javaExe，Windows 优先）。</summary>
    public static string JavaExe => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>某 javaHome 下 java 启动器的期望路径（不校验存在性）。</summary>
    public static string LauncherOf(string javaHome) => Path.Combine(javaHome, "bin", JavaExe);

    /// <summary>
    /// 该 javaHome 是否自带可执行的 java 启动器（<c>bin/java[.exe]</c>）——ADR-018 的核心校验。
    /// 残破目录（剥离启动器、路径非法、无权限）一律 false；这些目录不得被当作可用运行时。
    /// </summary>
    public static bool HasLauncher(string? javaHome)
    {
        if (string.IsNullOrWhiteSpace(javaHome)) return false;
        try
        {
            return File.Exists(LauncherOf(javaHome));
        }
        catch (Exception)
        {
            // 非法路径 / 重解析点损坏等：视为「不可用」，不得抛穿探测链
            return false;
        }
    }

    /// <summary>宿主环境变量 JAVA_HOME 的原值（未设置/空白 → null；不校验其内容）。</summary>
    public static string? HostJavaHomeEnv()
    {
        try
        {
            var v = Environment.GetEnvironmentVariable("JAVA_HOME");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// 宿主可用的 JDK 目录：仅接受**确有 java 启动器**的 JAVA_HOME。
    /// <para>
    /// 与 Java 版 <c>hostJavaHome()</c> 的语义差异（ADR-019）：Java 版在 JAVA_HOME 不可用时会退到
    /// 「当前 JVM 的 java.home」，C# 进程不是 Java 进程，没有这条退路——只剩 JAVA_HOME 一档。
    /// </para>
    /// </summary>
    public static string? HostJavaHome()
    {
        var env = HostJavaHomeEnv();
        return HasLauncher(env) ? env : null;
    }

    /// <summary>宿主可用的 java 可执行文件路径（<see cref="HostJavaHome"/> 下的 bin/java[.exe]；不可用则 null）。</summary>
    public static string? JavaLauncher()
    {
        var home = HostJavaHome();
        return home is null ? null : LauncherOf(home);
    }

    [GeneratedRegex(@"JAVA_VERSION=""([^"")]+)""")]
    private static partial Regex ReleaseVersionPattern();

    [GeneratedRegex(@"version ""([^""]+)""")]
    private static partial Regex VersionOutPattern();

    /// <summary>
    /// 指定 javaHome 的主版本：<c>release</c> 文件优先（不执行任何进程，快路径），
    /// 退化才跑 <c>java -version</c>；均失败返回 null。探测允许同步，但带 CancellationToken。
    /// </summary>
    public static int? MajorOf(string? javaHome, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(javaHome)) return null;

        var release = Path.Combine(javaHome, "release");
        try
        {
            if (File.Exists(release))
            {
                var m = ReleaseVersionPattern().Match(File.ReadAllText(release, Encoding.UTF8));
                if (m.Success)
                {
                    int major = ParseMajor(m.Groups[1].Value);
                    if (major > 0) return major;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 落到进程探测（对齐 Java 版 ignored 后 fromProcess 的语义）
        }
        return MajorFromProcess(javaHome, ct);
    }

    /// <summary>跑 <c>java -version</c> 解析主版本；失败/不可用 → null。取消请求向上抛。</summary>
    private static int? MajorFromProcess(string javaHome, CancellationToken ct)
    {
        var java = LauncherOf(javaHome);
        if (!File.Exists(java)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = java,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true, // java -version 走 stderr（Java 版 redirectErrorStream 等价）
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            try
            {
                var stdout = p.StandardOutput.ReadToEndAsync(ct);
                var stderr = p.StandardError.ReadToEndAsync(ct);
                if (!p.WaitForExit(15_000))
                {
                    try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                    return null;
                }
                try { Task.WhenAll(stdout, stderr).GetAwaiter().GetResult(); }
                catch (AggregateException) when (!ct.IsCancellationRequested) { /* 读不全则用已得部分 */ }
                var m = VersionOutPattern().Match(stdout.Result + "\n" + stderr.Result);
                if (!m.Success) return null;
                int v = ParseMajor(m.Groups[1].Value);
                return v > 0 ? v : null;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                throw;
            }
        }
        catch (CeobeException) { throw; }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 进程无法启动（依赖损坏、权限等）：视为探测失败，不抛穿
            return null;
        }
        catch (OperationCanceledException) { throw; }
    }

    /// <summary>
    /// "25.0.1"→25；"21.0.12.1"→21；"1.8.0_402"→8；"21-ea"→21；非法/null→0。
    /// 逐字对齐 Java 版 JavaEnv.parseMajor（含前导 "1." 的 JLS 旧记法规则）。
    /// </summary>
    public static int ParseMajor(string? version)
    {
        if (version is null) return 0;
        var v = version.Trim();
        if (v.StartsWith("1.", StringComparison.Ordinal))
        {
            int dot = v.IndexOf('.', 2);
            v = v.Substring(2, dot < 0 ? v.Length - 2 : dot - 2);
        }
        else
        {
            int dot = v.IndexOf('.');
            if (dot > 0) v = v[..dot];
            int dash = v.IndexOf('-');
            if (dash > 0) v = v[..dash];
        }
        return int.TryParse(v, out var n) ? n : 0;
    }
}
