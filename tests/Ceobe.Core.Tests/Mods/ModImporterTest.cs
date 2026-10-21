using System.IO.Compression;
using System.Text;
using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Mods;

/// <summary>
/// ModImporter 嗅探规则（整改项 4 的验收骨架）。
/// 全部离线：在临时目录构造假 jar / zip，不联网、不碰 CeobeHome.Override。
/// 逐条覆盖任务书给的决策表与「识别 → 预览 → 确认才落盘」红线。
/// </summary>
public sealed class ModImporterTest : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ceobe-import-" + Guid.NewGuid().ToString("N")[..8]);

    public ModImporterTest() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>造一个 zip/jar：entries 为「条目名 → 文本内容」，返回绝对路径。</summary>
    private string Zip(string fileName, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_dir, fileName);
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                if (name.EndsWith("/"))
                {
                    zip.CreateEntry(name);   // 目录条目
                    continue;
                }
                using var w = zip.CreateEntry(name).Open();
                w.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return path;
    }

    private static InstanceMeta Fabric(string gv = "26.3") =>
        new() { Name = "f", ServerType = ServerType.FABRIC, GameVersion = gv };

    // —— 决策表 ①②：mrpack 与四种模组标记 ——

    [Fact(DisplayName = "根含 modrinth.index.json → Mrpack，且预览不写盘")]
    public void Mrpack识别()
    {
        var pack = Zip("p.mrpack", ("modrinth.index.json",
            """{"formatVersion":1,"name":"P","versionId":"1","dependencies":{"minecraft":"26.3","fabric-loader":"0.19.5"},"files":[{"path":"mods/a.jar","env":{"client":"required","server":"required"},"downloads":["https://example.invalid/a.jar"],"size":11}]}"""));
        var plan = ModImporter.Inspect(pack);

        Assert.Equal(ModImporter.ImportKind.Mrpack, plan.Kind);
        Assert.Equal(ModImporter.ImportConfidence.Confirmed, plan.Confidence);
        Assert.Equal("fabric", plan.DetectedLoader);
        Assert.Equal("26.3", plan.DetectedGameVersion);
        Assert.Contains("modrinth.index.json", string.Join(" ", plan.Evidence));
        Assert.True(plan.Actionable);
        // 索引声明的 files[] 进入清单
        Assert.Single(plan.Files);
        Assert.Equal("mods/a.jar", plan.Files[0].RelativeTarget);
        // 预览绝不写盘：除源文件外目录里不该多出任何东西
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [Fact(DisplayName = "fabric.mod.json → FabricMod，建议落 mods/")]
    public void FabricMod识别()
    {
        var jar = Zip("fabricmod.jar",
            ("fabric.mod.json", """{"id":"m","version":"1","depends":{"minecraft":"26.3"},"environment":"*"}"""));
        var plan = ModImporter.Inspect(jar);

        Assert.Equal(ModImporter.ImportKind.FabricMod, plan.Kind);
        Assert.Equal("mods", plan.SuggestedSubdir);
        Assert.Equal("fabric", plan.DetectedLoader);
        Assert.Equal("26.3", plan.DetectedGameVersion);
        Assert.True(plan.Actionable);
    }

    [Fact(DisplayName = "META-INF/neoforge.mods.toml 优先于 META-INF/mods.toml")]
    public void NeoForge优先于Forge()
    {
        // 同一个 jar 同时带两个标记（NeoForge 1.20.5+ 的实际形态）：neoforge 必须先命中
        var jar = Zip("nf.jar",
            ("META-INF/mods.toml", "modId=\"legacy\""),
            ("META-INF/neoforge.mods.toml", "modId=\"nf\"\nloaderVersion=\"[4.0,)\""));
        var plan = ModImporter.Inspect(jar);
        Assert.Equal(ModImporter.ImportKind.NeoForgeMod, plan.Kind);
        Assert.Equal("neoforge", plan.DetectedLoader);
    }

    [Fact(DisplayName = "META-INF/mods.toml → ForgeMod")]
    public void ForgeMod识别()
    {
        var jar = Zip("forge.jar", ("META-INF/mods.toml", "modId=\"f\"\nmcVersion=\"1.20.1\"\n"));
        var plan = ModImporter.Inspect(jar);
        Assert.Equal(ModImporter.ImportKind.ForgeMod, plan.Kind);
        Assert.Equal("forge", plan.DetectedLoader);
        Assert.Equal("1.20.1", plan.DetectedGameVersion);
    }

    [Fact(DisplayName = "mcmod.info → LegacyForgeMod 并读出 mcversion")]
    public void 旧Forge识别()
    {
        var jar = Zip("legacy.jar", ("mcmod.info", """[{"modid":"x","version":"1","mcversion":"1.12.2"}]"""));
        var plan = ModImporter.Inspect(jar);
        Assert.Equal(ModImporter.ImportKind.LegacyForgeMod, plan.Kind);
        Assert.Equal("forge", plan.DetectedLoader);
        Assert.Equal("1.12.2", plan.DetectedGameVersion);
    }

    // —— 决策表 ③④ ——

    [Fact(DisplayName = "含 mods/ 目录 → ModsBundle，清单取 mods/ 下文件")]
    public void ModsBundle识别()
    {
        var zip = Zip("bundle.zip",
            ("mods/", ""),
            ("mods/one.jar", "aaaaaaaa"),          // 8 字节
            ("mods/nested/two.jar", "bbb"),        // 3 字节，保留相对路径
            ("config/x.toml", "y=1"));
        var plan = ModImporter.Inspect(zip);

        Assert.Equal(ModImporter.ImportKind.ModsBundle, plan.Kind);
        Assert.Equal("mods", plan.SuggestedSubdir);
        Assert.Equal(2, plan.Files.Count);
        Assert.Contains(plan.Files, f => f.RelativeTarget == "mods/one.jar");
        Assert.Contains(plan.Files, f => f.RelativeTarget == "mods/nested/two.jar");
        Assert.Equal(11, plan.TotalBytes);
        Assert.True(plan.Actionable);
    }

    [Fact(DisplayName = "顶层 version.json（带 formatVersion）→ MultiMcPack 且不落盘")]
    public void MultiMc识别()
    {
        var zip = Zip("mmc.zip",
            ("mods/", ""),
            ("mods/a.jar", "zzz"),
            ("version.json", """{"formatVersion":1,"minecraftVersion":"1.20.1"}"""));
        var plan = ModImporter.Inspect(zip);

        // 任务书顺序：mods/ 在 version.json 之前判定 → 更可行动的合集分支优先
        Assert.Equal(ModImporter.ImportKind.ModsBundle, plan.Kind);

        var pure = Zip("mmc-pure.zip", ("version.json", """{"formatVersion":1}"""));
        var p2 = ModImporter.Inspect(pure);
        Assert.Equal(ModImporter.ImportKind.MultiMcPack, p2.Kind);
        Assert.False(p2.Actionable);
        Assert.Contains(p2.Notes, n => n.Contains("暂不完整支持"));
    }

    [Fact(DisplayName = "manifest.yml → MultiMcPack")]
    public void MultiMcManifestYml()
    {
        var zip = Zip("pcl.zip", ("manifest.yml", "name: x"));
        var plan = ModImporter.Inspect(zip);
        Assert.Equal(ModImporter.ImportKind.MultiMcPack, plan.Kind);
        Assert.False(plan.Actionable);
    }

    // —— 决策表 ⑤：全未命中 ——

    [Fact(DisplayName = "无任何标记的 jar → ServerJarCandidate，标出而不是断言")]
    public void 服务端jar候选()
    {
        var jar = Zip("plain.jar", ("Main.class", "x"));
        var plan = ModImporter.Inspect(jar);

        Assert.Equal(ModImporter.ImportKind.ServerJarCandidate, plan.Kind);
        Assert.NotEqual(ModImporter.ImportConfidence.Confirmed, plan.Confidence);
        Assert.False(plan.Actionable);
        Assert.Contains(plan.Notes, n => n.Contains("可能是服务端 jar"));
        // 原版服务端 jar 也带 version.json，但不能因此判成 MultiMC
        var vanilla = Zip("server.jar",
            ("version.json", """{"id":"26.3","javaVersion":25}"""),
            ("server.jar", "x"));
        Assert.Equal(ModImporter.ImportKind.ServerJarCandidate, ModImporter.Inspect(vanilla).Kind);
    }

    [Fact(DisplayName = "读不了 zip → Unknown，需用户指定用途而不是抛异常")]
    public void 未知类型不抛异常()
    {
        var txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");
        var plan = ModImporter.Inspect(txt);
        Assert.Equal(ModImporter.ImportKind.Unknown, plan.Kind);
        Assert.Equal(ModImporter.ImportConfidence.Ambiguous, plan.Confidence);
        Assert.False(plan.Actionable);
    }

    [Fact(DisplayName = "包内 dependencies/ → 提示可能缺失依赖")]
    public void 依赖目录提示()
    {
        var jar = Zip("dep.jar",
            ("fabric.mod.json", """{"id":"m","depends":{"minecraft":"*"},"environment":"*"}"""),
            ("dependencies/library.jar", "x"));
        var plan = ModImporter.Inspect(jar);
        Assert.Contains(plan.Notes, n => n.Contains("dependencies/"));
    }

    [Fact(DisplayName = "包内 libraries/ → 提示可能缺失依赖")]
    public void 库目录提示()
    {
        var jar = Zip("lib.jar",
            ("META-INF/mods.toml", "modId=\"f\""),
            ("libraries/foo.jar", "x"));
        var plan = ModImporter.Inspect(jar);
        Assert.Contains(plan.Notes, n => n.Contains("libraries/"));
    }

    // —— 匹配判断：保守，不确定不返回乐观 true ——

    [Fact(DisplayName = "fabric 模组 + fabric 实例 + 精确同版本 → Matches")]
    public void 匹配_精确同版本()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"26.3"},"environment":"*"}"""));
        var plan = ModImporter.Inspect(jar, Fabric("26.3"));
        Assert.Equal(ModImporter.MatchVerdict.Matches, plan.Match);
    }

    [Fact(DisplayName = "版本为范围/通配/缺失 → Unknown 而不是乐观 Matches")]
    public void 匹配_版本不确定()
    {
        foreach (var depends in new[] { """{"minecraft":"*"}""", """{"minecraft":"~26.3"}""",
                     """{"minecraft":"[1.20,1.21)"}""", "{}"})
        {
            var jar = Zip("m-" + Guid.NewGuid().ToString("N")[..6] + ".jar",
                ("fabric.mod.json", $$"""{"depends":{{depends}},"environment":"*"}"""));
            var plan = ModImporter.Inspect(jar, Fabric("26.3"));
            Assert.Equal(ModImporter.MatchVerdict.Unknown, plan.Match);
        }
    }

    [Fact(DisplayName = "版本明确不同 → Mismatch")]
    public void 匹配_版本不同()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"1.20.1"},"environment":"*"}"""));
        var plan = ModImporter.Inspect(jar, Fabric("26.3"));
        Assert.Equal(ModImporter.MatchVerdict.Mismatch, plan.Match);
    }

    [Fact(DisplayName = "fabric 模组装 paper 实例 → Mismatch（无模组加载器）")]
    public void 匹配_实例无加载器()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"*"},"environment":"*"}"""));
        var plan = ModImporter.Inspect(jar,
            new InstanceMeta { Name = "p", ServerType = ServerType.PAPER, GameVersion = "26.3" });
        Assert.Equal(ModImporter.MatchVerdict.Mismatch, plan.Match);
    }

    [Fact(DisplayName = "environment=client 的 fabric 模组 → Mismatch")]
    public void 匹配_客户端专属()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"26.3"},"environment":"client"}"""));
        var plan = ModImporter.Inspect(jar, Fabric("26.3"));
        Assert.Equal(ModImporter.MatchVerdict.Mismatch, plan.Match);
        Assert.Contains("客户端专属", plan.MatchReason);
    }

    [Fact(DisplayName = "forge 模组 + fabric 实例 → Mismatch；+ neoforge 实例 → Unknown（不乐观）")]
    public void 匹配_forge跨加载器()
    {
        var jar = Zip("f.jar", ("META-INF/mods.toml", "modId=\"f\"\nmcVersion=\"26.3\"\n"));
        Assert.Equal(ModImporter.MatchVerdict.Mismatch, ModImporter.Inspect(jar, Fabric("26.3")).Match);

        var neo = new InstanceMeta
        {
            Name = "n", ServerType = ServerType.NEOFORGE, GameVersion = "26.3", LoaderVersion = "4.0.0",
        };
        Assert.Equal(ModImporter.MatchVerdict.Unknown, ModImporter.Inspect(jar, neo).Match);
    }

    [Fact(DisplayName = "整合包版本/加载器不匹配 → Mismatch；声明缺失 → Unknown")]
    public void 匹配_整合包()
    {
        var wrong = Zip("w.mrpack", ("modrinth.index.json",
            """{"name":"W","versionId":"1","dependencies":{"minecraft":"1.20.1","neoforge":"4.0.0"},"files":[]}"""));
        var p1 = ModImporter.Inspect(wrong, Fabric("26.3"));
        Assert.Equal(ModImporter.MatchVerdict.Mismatch, p1.Match);

        var vague = Zip("v.mrpack", ("modrinth.index.json", """{"name":"V","versionId":"1","dependencies":{},"files":[]}"""));
        var p2 = ModImporter.Inspect(vague, Fabric("26.3"));
        Assert.Equal(ModImporter.MatchVerdict.Unknown, p2.Match);

        var quilt = Zip("q.mrpack", ("modrinth.index.json",
            """{"name":"Q","versionId":"1","dependencies":{"minecraft":"26.3","quilt-loader":"0.20.0"},"files":[]}"""));
        var p3 = ModImporter.Inspect(quilt, Fabric("26.3"));
        Assert.False(p3.Actionable);
        Assert.Equal(ModImporter.MatchVerdict.Mismatch, p3.Match);
        Assert.Contains("暂不支持", p3.MatchReason);
    }

    [Fact(DisplayName = "未提供实例信息时 Match 保持 Unknown")]
    public void 匹配_无实例信息()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"26.3"},"environment":"*"}"""));
        Assert.Equal(ModImporter.MatchVerdict.Unknown, ModImporter.Inspect(jar).Match);
    }

    // —— 预览/执行分离：InstallAsync 的落盘与拒绝 ——

    [Fact(DisplayName = "预览不落盘，执行才写入 mods/ 并产出记录")]
    public async Task 预览与执行分离()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"26.3"},"environment":"*"}"""));
        var instance = Path.Combine(_dir, "inst");
        Directory.CreateDirectory(instance);

        var plan = ModImporter.Inspect(jar, Fabric("26.3"), instance);
        Assert.Equal(ModImporter.MatchVerdict.Matches, plan.Match);
        Assert.False(Directory.Exists(Path.Combine(instance, "mods"))); // 预览没建目录

        var r = await ModImporter.InstallAsync(plan, instance, progress: NullProgress.Instance);
        Assert.Equal(1, r.WrittenFiles);
        var landed = Path.Combine(instance, "mods", "m.jar");
        Assert.True(File.Exists(landed));
        Assert.Single(r.Records);
        Assert.Equal("m.jar", r.Records[0].FileName);
        Assert.Equal(HttpUtil.Sha1(landed), r.Records[0].Sha1);
    }

    [Fact(DisplayName = "重名文件在预览里标注 AlreadyExists，执行时覆盖")]
    public async Task 重名提示与覆盖()
    {
        var jar = Zip("dup.jar", ("fabric.mod.json", """{"depends":{"minecraft":"*"},"environment":"*"}"""));
        var instance = Path.Combine(_dir, "inst-dup");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        await File.WriteAllTextAsync(Path.Combine(instance, "mods", "dup.jar"), "OLD");

        var plan = ModImporter.Inspect(jar, Fabric("26.3"), instance);
        Assert.True(plan.Files[0].AlreadyExists);
        Assert.Contains(plan.Notes, n => n.Contains("同名文件"));

        await ModImporter.InstallAsync(plan, instance, progress: NullProgress.Instance);
        // 覆盖语义：目标文件应变成源 jar 内容（不再是 OLD）
        var after = await File.ReadAllBytesAsync(Path.Combine(instance, "mods", "dup.jar"));
        Assert.Equal(await File.ReadAllBytesAsync(jar), after);
    }

    [Fact(DisplayName = "合集安装：mods/ 相对路径保留，记录逐条产出")]
    public async Task 合集安装落盘()
    {
        var zip = Zip("bundle.zip", ("mods/", ""), ("mods/one.jar", "one"), ("mods/sub/two.jar", "tw"));
        var instance = Path.Combine(_dir, "inst-bundle");
        Directory.CreateDirectory(instance);

        var plan = ModImporter.Inspect(zip);
        var r = await ModImporter.InstallAsync(plan, instance, progress: NullProgress.Instance);

        Assert.Equal(2, r.WrittenFiles);
        Assert.True(File.Exists(Path.Combine(instance, "mods", "one.jar")));
        Assert.Equal("tw", await File.ReadAllTextAsync(Path.Combine(instance, "mods", "sub", "two.jar")));
        Assert.Equal(2, r.Records.Count);
    }

    [Fact(DisplayName = "Mismatch 默认拒绝；显式 allowMismatch 才强行落盘")]
    public async Task 不匹配需显式确认()
    {
        var jar = Zip("m.jar", ("fabric.mod.json", """{"depends":{"minecraft":"1.20.1"},"environment":"*"}"""));
        var instance = Path.Combine(_dir, "inst-guard");
        Directory.CreateDirectory(instance);
        var plan = ModImporter.Inspect(jar, Fabric("26.3"), instance);

        await Assert.ThrowsAsync<CeobeException>(() =>
            ModImporter.InstallAsync(plan, instance, progress: NullProgress.Instance));
        Assert.False(Directory.Exists(Path.Combine(instance, "mods")));

        await ModImporter.InstallAsync(plan, instance, allowMismatch: true, progress: NullProgress.Instance);
        Assert.True(File.Exists(Path.Combine(instance, "mods", "m.jar")));
    }

    [Fact(DisplayName = "不可行动的类型（MultiMc / 服务端候选 / Unknown）执行时抛业务异常")]
    public async Task 不可行动类型拒绝执行()
    {
        foreach (var path in new[]
                 {
                     Zip("a.zip", ("manifest.yml", "name: x")),
                     Zip("b.jar", ("Main.class", "x")),
                 })
        {
            var plan = ModImporter.Inspect(path);
            Assert.False(plan.Actionable);
            await Assert.ThrowsAsync<CeobeException>(() =>
                ModImporter.InstallAsync(plan, _dir, progress: NullProgress.Instance));
        }
    }

    [Fact(DisplayName = "合集安装拒绝穿越条目，不写出实例目录之外")]
    public async Task 合集防目录穿越()
    {
        // 手工构造含 ../ 的 mods/ 条目（ZipArchive.CreateEntry 允许这种名字），带 .jar 扩展名以进入清单
        var zip = Path.Combine(_dir, "evil.zip");
        using (var fs = File.Create(zip))
        using (var a = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = a.CreateEntry("mods/../../evil.jar").Open();
            w.Write("x"u8);
        }
        var outside = Path.Combine(_dir, "evil.jar");
        var instance = Path.Combine(_dir, "inst-evil");
        Directory.CreateDirectory(instance);

        var plan = ModImporter.Inspect(zip);
        var r = await ModImporter.InstallAsync(plan, instance, progress: NullProgress.Instance);
        Assert.Equal(0, r.WrittenFiles);          // 穿越条目被拒
        Assert.False(File.Exists(outside));       // 实例目录之外没有写出任何文件
    }
}
