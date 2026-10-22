using System.IO.Compression;
using System.Text;
using Ceobe.Core.Client;
using Ceobe.Core.Store;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// mods 目录扫描与「依赖健康」判定。真实场景是 Modrinth/整合包塞进来的各种 jar，
/// 这里用最小化的 fabric.mod.json / mods.toml 复现三类情况：齐全、缺前置、停用后不算数。
/// </summary>
public class ClientModScannerTest : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;

    public ClientModScannerTest()
    {
        _home = Path.Combine(Path.GetTempPath(), "ceobe-client-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
        _previous = CeobeHome.Override;
        CeobeHome.Override = _home;
    }

    public void Dispose()
    {
        CeobeHome.Override = _previous;
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    private static string MakeJar(string home, string client, string fileName, string json)
    {
        var dir = ClientStore.ModsDir(client);
        var path = Path.Combine(dir, fileName);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("fabric.mod.json");
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(json);
        return path;
    }

    [Fact(DisplayName = "前置齐全：Satisfied 列出 sodium，Health 不再是缺失")]
    public void 前置齐全()
    {
        ClientStore.Create(new ClientMeta { Name = "生存重启", GameVersion = "1.20.1", Loader = ClientLoader.FABRIC, LoaderVersion = "0.15.11" });
        MakeJar(_home, "生存重启", "sodium.jar",
            """{"id":"sodium","name":"Sodium","version":"0.5.11","depends":{"minecraft":"1.20.1"}}""");
        MakeJar(_home, "生存重启", "iris.jar",
            """{"id":"iris","name":"Iris Shaders","version":"1.7.1","depends":{"minecraft":"1.20.1","sodium":">=0.5"}}""");

        var mods = ClientModScanner.Scan("生存重启");
        var iris = mods.Single(m => m.ModId == "iris");
        Assert.Contains("sodium", iris.Satisfied);
        Assert.Empty(iris.Missing);
        Assert.Equal("需要 sodium（已满足）", iris.Health);
    }

    [Fact(DisplayName = "缺前置：Missing 命中，Health 写成可读文案")]
    public void 缺前置()
    {
        ClientStore.Create(new ClientMeta { Name = "生存重启", GameVersion = "1.20.1", Loader = ClientLoader.FABRIC, LoaderVersion = "0.15.11" });
        MakeJar(_home, "生存重启", "create.jar",
            """{"id":"create","name":"Create","version":"0.5.1f","depends":{"minecraft":"1.20.1","flywheel":"*"}}""");

        var mods = ClientModScanner.Scan("生存重启");
        var create = mods.Single();
        Assert.Equal(new[] { "flywheel" }, create.Missing);
        Assert.Equal("缺少前置 flywheel", create.Health);
    }

    [Fact(DisplayName = "停用的 Mod 不算已满足依赖")]
    public void 停用不算满足()
    {
        ClientStore.Create(new ClientMeta { Name = "a", GameVersion = "1.20.1", Loader = ClientLoader.FABRIC, LoaderVersion = "0.15.11" });
        MakeJar(_home, "a", "sodium.jar", """{"id":"sodium","name":"Sodium","version":"0.5.11"}""");
        MakeJar(_home, "a", "iris.jar", """{"id":"iris","name":"Iris","version":"1.7.1","depends":{"sodium":"*"}}""");

        ClientModScanner.SetEnabled("a", "sodium.jar", false);

        var mods = ClientModScanner.Scan("a");
        var iris = mods.Single(m => m.ModId == "iris");
        Assert.Contains("sodium", iris.Missing);
        Assert.True(mods.Single(m => m.ModId == "sodium").Enabled == false);
    }

    [Fact(DisplayName = "Forge 的 mods.toml 也能读出 id 与依赖")]
    public void 解析toml()
    {
        var toml = """
            modLoader="javafml"
            loaderVersion="[47,)"
            license="MIT"
            [[mods]]
            modId="create"
            version="0.5.1f"
            displayName="Create"

            [[dependencies.create]]
            modId="minecraft"
            mandatory=true
            versionRange="[1.20.1]"
            [[dependencies.create]]
            modId="flywheel"
            mandatory=true
            versionRange="[0.6.10,)"
            """;

        var (id, name, version, depends) = ClientModScanner.ParseToml(toml);
        Assert.Equal("create", id);
        Assert.Equal("Create", name);
        Assert.Equal("0.5.1f", version);
        Assert.Contains(depends, d => d.Id == "flywheel");
    }
}
