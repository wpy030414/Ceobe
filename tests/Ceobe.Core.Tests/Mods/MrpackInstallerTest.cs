using System.IO.Compression;
using System.Text;
using Ceobe.Core.Mods;
using Xunit;

namespace Ceobe.Core.Tests.Mods;

/// <summary>
/// 整合包索引解析（离线：构造合成 .mrpack）。
/// 逐条移植自 Java 版 MrpackInstallerTest；另补 InstallFromLocal 的本地路径语义。
/// </summary>
public class MrpackInstallerTest
{
    private static string WriteMrpack(string dir, string indexJson)
    {
        var zip = Path.Combine(dir, "test.mrpack");
        using (var fs = File.Create(zip))
        using (var outStream = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = outStream.CreateEntry("modrinth.index.json").Open())
            {
                w.Write(Encoding.UTF8.GetBytes(indexJson));
            }
            using (var w = outStream.CreateEntry("overrides/config/foo.toml").Open())
            {
                w.Write("x=1"u8);
            }
        }
        return zip;
    }

    [Fact(DisplayName = "解析 fabric 依赖")]
    public void 解析fabric依赖()
    {
        var dir = TempDir();
        var json = """
            {"formatVersion":1,"name":"Test Pack","versionId":"1.2.0",
             "dependencies":{"minecraft":"26.3","fabric-loader":"0.19.5"},
             "files":[]}
            """;
        var idx = MrpackInstaller.ReadIndex(WriteMrpack(dir, json));
        Assert.Equal("Test Pack", idx.Name);
        Assert.Equal("1.2.0", idx.VersionId);
        Assert.Equal("26.3", idx.McVersion);
        Assert.Equal("fabric", idx.Loader);
        Assert.Equal("0.19.5", idx.LoaderVersion);
    }

    [Fact(DisplayName = "解析 neoforge 依赖")]
    public void 解析neoforge依赖()
    {
        var dir = TempDir();
        var json = """
            {"formatVersion":1,"name":"N","versionId":"9",
             "dependencies":{"minecraft":"26.2","neoforge":"26.2.0.93"},
             "files":[]}
            """;
        var idx = MrpackInstaller.ReadIndex(WriteMrpack(dir, json));
        Assert.Equal("neoforge", idx.Loader);
        Assert.Equal("26.2.0.93", idx.LoaderVersion);
    }

    [Fact(DisplayName = "缺少索引时抛错")]
    public void 缺少索引时抛错()
    {
        var dir = TempDir();
        var zip = Path.Combine(dir, "bad.mrpack");
        using (var fs = File.Create(zip))
        using (var outStream = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = outStream.CreateEntry("readme.txt").Open();
            w.Write("x"u8);
        }
        Assert.ThrowsAny<Exception>(() => MrpackInstaller.ReadIndex(zip));
    }

    [Fact(DisplayName = "读索引后临时目录被清理")]
    public void 读索引后临时目录被清理()
    {
        var dir = TempDir();
        var json = """{"name":"T","versionId":"1","dependencies":{"minecraft":"26.3","fabric-loader":"0.1"},"files":[]}""";
        var pack = WriteMrpack(dir, json);
        MrpackInstaller.ReadIndex(pack);
        Assert.False(Directory.Exists(pack + ".index"));
    }

    [Fact(DisplayName = "本地文件不存在时给业务异常")]
    public async Task 本地文件不存在时给业务异常()
    {
        var dir = TempDir();
        await Assert.ThrowsAsync<Ceobe.Core.Util.CeobeException>(
            () => MrpackInstaller.InstallFromLocalAsync(dir, Path.Combine(dir, "nope.mrpack")));
    }

    [Fact(DisplayName = "零文件整合包可离线安装并落 overrides")]
    public async Task 零文件整合包可离线安装并落overrides()
    {
        // 不联网的 InstallCore 覆盖路径：files 全为 unsupported → 跳过；overrides 照常落地
        var dir = TempDir();
        var json = """
            {"formatVersion":1,"name":"No Files","versionId":"0.1",
             "dependencies":{"minecraft":"26.3","fabric-loader":"0.19.5"},
             "files":[{"env":{"client":"required","server":"unsupported"},"path":"mods/client-only.jar",
                       "hashes":{"sha1":"00"},"downloads":["https://example.invalid/a.jar"]}]}
            """;
        var pack = WriteMrpack(dir, json);
        var instance = Path.Combine(dir, "inst");
        Directory.CreateDirectory(instance);

        var r = await MrpackInstaller.InstallFromLocalAsync(instance, pack,
            Ceobe.Core.Util.NullProgress.Instance);
        Assert.Equal("No Files", r.Name);
        Assert.Equal(0, r.Files);
        Assert.Equal(1, r.Skipped);          // env.server=unsupported 被跳过（硬契约 6）
        Assert.Equal(1, r.Overrides);        // overrides/config/foo.toml 落地
        Assert.True(File.Exists(Path.Combine(instance, "config", "foo.toml")));
        Assert.False(Directory.Exists(Path.Combine(instance, ".mrpack-work"))); // 工作目录已清理
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-mrpack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
