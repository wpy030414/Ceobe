using System.IO.Compression;
using System.Runtime.InteropServices;
using Ceobe.Core.Jdk;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Jdk;

/// <summary>
/// 安装层：解压逻辑用假 zip 归档覆盖（任务约束：不下载真实 JDK）；平台字符串对齐 Adoptium API 词汇。
/// 与 JdkStore 扫描共享进程级静态 Override，故串行（见 <see cref="JdkSerialTestBase"/>）。
/// </summary>
public sealed class JdkInstallerTest : JdkSerialTestBase
{
    /// <summary>造假归档 zip（顶层目录含/不含 bin/java[.exe]），附一条目录穿越条目，返回归档路径。</summary>
    private string MakeFakeZip(string topDir, bool withLauncher)
    {
        var zipPath = Path.Combine(Home, "FakeJDK.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // ZipArchive 同一时刻只允许一个条目流打开：逐条 create→write→close
            if (withLauncher)
            {
                using var w = zip.CreateEntry($"{topDir}/bin/{JavaEnv.JavaExe}").Open();
                w.Write("#fake launcher"u8);
            }
            using (var rw = zip.CreateEntry($"{topDir}/release").Open())
            {
                rw.Write("JAVA_VERSION=\"21.0.1\"\n"u8);
            }
            // 目录穿越条目：ZipUtil.Extract 必须拒绝（spec 约束「entry 归一化后须仍在目标目录内」）
            zip.CreateEntry("../evil.txt");
        }
        return zipPath;
    }

    [Fact]
    public async Task 假zip解压_产出可用javaHome()
    {
        var zip = MakeFakeZip("jdk-21.0.1+12", withLauncher: true);
        var home = await JdkInstaller.ExtractAsync(zip, CancellationToken.None);

        Assert.Equal(Path.Combine(JdkStore.Root(), "jdk-21.0.1+12"), home);
        Assert.True(JavaEnv.HasLauncher(home));                     // ADR-018 守卫
        Assert.Equal(21, JavaEnv.MajorOf(home));
        Assert.False(File.Exists(Path.Combine(Home, "evil.txt")));  // 穿越条目未逃逸到归档同级
    }

    [Fact]
    public async Task 假zip缺启动器_解压失败且给出下一步()
    {
        var zip = MakeFakeZip("jdk-stripped-9", withLauncher: false);
        var ex = await Assert.ThrowsAsync<CeobeException>(
            () => JdkInstaller.ExtractAsync(zip, CancellationToken.None));
        Assert.Contains("bin/", ex.Message);
        Assert.Contains("下一步", ex.Message);
    }

    [Fact]
    public void 平台名对齐Adoptium词汇表()
    {
        // Adoptium API 的 os ∈ {windows, linux, mac}、architecture ∈ {x64, aarch64}
        Assert.Contains(JdkInstaller.OsName(), new[] { "windows", "linux", "mac" });
        Assert.Contains(JdkInstaller.ArchName(), new[] { "x64", "aarch64" });
        if (OperatingSystem.IsWindows()) Assert.Equal("windows", JdkInstaller.OsName());
        if (RuntimeInformation.OSArchitecture == Architecture.X64) Assert.Equal("x64", JdkInstaller.ArchName());
    }

    [Fact]
    public void JdkStore根目录在ceobe_home下的jdk()
    {
        // 闸门 B：路径必须与 Java 版 ~/.ceobe/jdk 一致（Java 源码为权威，非任务描述的 jdks/）
        Assert.Equal(Path.Combine(Home, "jdk"), JdkStore.Root());
    }

    [Fact]
    public void JdkStore_过滤残破目录并按主版本降序()
    {
        var root = JdkStore.Root();
        WriteFake(root, "jdk-25", 25, true);
        WriteFake(root, "jdk-17", 17, true);
        var broken = Path.Combine(root, "jdk-broken");
        Directory.CreateDirectory(Path.Combine(broken, "bin")); // 无启动器 → 不入列

        var list = JdkStore.List();
        Assert.DoesNotContain(list, j => j.Name == "jdk-broken");
        Assert.Equal(new[] { 25, 17 }, list.Select(j => j.Major).ToArray()); // 降序
        Assert.Equal(Path.Combine(root, "jdk-17"), JdkStore.FindMajor(17));
        Assert.Null(JdkStore.FindMajor(8));
    }

    private static void WriteFake(string root, string name, int major, bool withLauncher)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        if (withLauncher) File.WriteAllText(Path.Combine(dir, "bin", JavaEnv.JavaExe), "#fake");
        File.WriteAllText(Path.Combine(dir, "release"), $"JAVA_VERSION=\"{major}.0.1\"\n");
    }
}
