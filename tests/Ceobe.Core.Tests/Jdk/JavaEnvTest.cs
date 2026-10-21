using Ceobe.Core.Jdk;
using Xunit;

namespace Ceobe.Core.Tests.Jdk;

/// <summary>
/// Java 主版本解析与启动器可用性判定。
/// 逐条移植自 Java 版 JavaEnvTest（验收标准第 4 条），并按迁移任务补充三态判定用例。
/// </summary>
public class JavaEnvTest
{
    [Theory]
    [InlineData("25.0.1", 25)]
    [InlineData("21.0.12.1", 21)]
    [InlineData("17.0.20.1", 17)]
    [InlineData("1.8.0_402", 8)]
    [InlineData("21-ea", 21)]
    [InlineData("abc", 0)]
    [InlineData(null, 0)]
    public void 解析主版本(string? version, int expected)
    {
        Assert.Equal(expected, JavaEnv.ParseMajor(version));
    }

    /// <summary>三态判定（任务要求）：有 bin/java / 缺 bin/java / 目录不存在，外加 null。</summary>
    [Fact]
    public void HasLauncher_三态判定()
    {
        var root = Path.Combine(Path.GetTempPath(), "ceobe-javaenv-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // ① 完整 JDK：bin/java[.exe] 存在 → true
            var complete = Path.Combine(root, "jdk-complete");
            Directory.CreateDirectory(Path.Combine(complete, "bin"));
            File.WriteAllText(Path.Combine(complete, "bin", JavaEnv.JavaExe), "#fake launcher");
            Assert.True(JavaEnv.HasLauncher(complete));

            // ② 残破目录（jpackage 剥离启动器的形态）：只有空 bin → false
            var stripped = Path.Combine(root, "jdk-stripped");
            Directory.CreateDirectory(Path.Combine(stripped, "bin"));
            Assert.False(JavaEnv.HasLauncher(stripped));

            // ③ 目录不存在 / null / 空白 → false
            Assert.False(JavaEnv.HasLauncher(Path.Combine(root, "nope")));
            Assert.False(JavaEnv.HasLauncher(null));
            Assert.False(JavaEnv.HasLauncher("  "));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>majorOf 走 release 文件快路径，不执行任何进程。</summary>
    [Fact]
    public void MajorOf_读release文件()
    {
        var root = Path.Combine(Path.GetTempPath(), "ceobe-javamajor-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var home = Path.Combine(root, "jdk");
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            File.WriteAllText(Path.Combine(home, "bin", JavaEnv.JavaExe), "#fake");
            File.WriteAllText(Path.Combine(home, "release"),
                "JAVA_VERSION=\"21.0.12.1\"\nOS=\"windows\"\n");
            Assert.Equal(21, JavaEnv.MajorOf(home));

            // 无 release 且启动器是假文件：进程探测必然失败 → null（不抛）
            File.Delete(Path.Combine(home, "release"));
            Assert.Null(JavaEnv.MajorOf(home));

            // null 输入
            Assert.Null(JavaEnv.MajorOf(null));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Windows 语义：启动器带 .exe（代码不写死平台，判定按当前 OS）。</summary>
    [Fact]
    public void JavaExe_按平台带后缀()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "java.exe" : "java", JavaEnv.JavaExe);
        Assert.EndsWith(JavaEnv.JavaExe, JavaEnv.LauncherOf("C:\\x"));
    }
}
