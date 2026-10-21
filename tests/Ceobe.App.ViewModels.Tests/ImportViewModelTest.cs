using System.IO.Compression;
using System.Text;
using Ceobe.App.ViewModels;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;
using static Ceobe.Core.Mods.ModImporter;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「一键导入」确认页 ViewModel 验收（整改项 4）。
///
/// 训练员原话：「mod下载和整合包下载我没看懂怎么用……建议设置成一键导入的模式，
/// mod接受对应的jar文件，整合包接受对应的压缩包文件」。
///
/// 本测试守的核心承诺是**绝不静默猜测**：静默猜错的代价是污染实例目录——
/// 把整合包误当 mod 塞进 mods/，用户要么启动崩溃，要么得手工翻几十个文件删干净。
/// 故每个识别分支都要验证三件事：给了什么预览、放行还是拦下、拦下时有没有说清下一步。
///
/// 全部离线：假归档在本机临时目录构造，不联网、不下载。
/// </summary>
[Collection("CeobeHome serial")]
public class ImportViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-imp-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _files =
        Path.Combine(Path.GetTempPath(), "ceobe-imp-files-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public ImportViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_files);
        CeobeHome.Override = _home;
        _dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(_dir);
        WriteMeta(ServerType.FABRIC, "26.3");
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
        ZipUtil.DeleteRecursively(_files);
    }

    private void WriteMeta(ServerType type, string version, int port = 25565) =>
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = _instance, ServerType = type, GameVersion = version, Port = port,
        }));

    /// <summary>造一个 zip/jar：entries 为「条目名 → 文本内容」。</summary>
    private string Zip(string fileName, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_files, fileName);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            if (name.EndsWith("/"))
            {
                zip.CreateEntry(name);
                continue;
            }
            using var w = zip.CreateEntry(name).Open();
            w.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    private ImportViewModel NewVm() => new(new InstanceManager(new FakeLocator()), _instance);

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    private string FabricModJar(string loaderVersion = "26.3") => Zip("mod.jar",
        // 用字符串拼接而非 $$""" 原始插值：JSON 结尾的 }} 会与 $$ 的插值定界符冲突（需 $$$），
        // 拼接更直观也不易踩这个坑。
        ("fabric.mod.json",
            "{\"id\":\"testmod\",\"version\":\"1.0\",\"environment\":\"*\"," +
            "\"depends\":{\"fabricloader\":\">=0.1\",\"minecraft\":\"" + loaderVersion + "\"}}"),
        ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0"));

    // ========== 识别与预览 ==========

    [Fact(DisplayName = "识别 fabric 模组 jar：种类/依据/落位齐全且可导入")]
    public async Task 识别fabric模组()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());

        Assert.True(vm.Inspected);
        Assert.False(vm.HasError);
        Assert.Equal(ImportKind.FabricMod, vm.Kind);
        Assert.Equal("Fabric 模组", vm.KindLabel);
        Assert.Equal("判定确定", vm.ConfidenceLabel);
        Assert.True(vm.Actionable);
        Assert.True(vm.CanInstall);
        Assert.Equal("mods", vm.SuggestedSubdir);
        Assert.NotEmpty(vm.Evidence);          // 必须摊开判定依据，不能只给结论
        Assert.Contains("fabric.mod.json", string.Join(" ", vm.Evidence));
        Assert.Equal(1, vm.FileCount);
        Assert.True(vm.TotalBytes > 0);
    }

    [Fact(DisplayName = "识别只读：预演后实例目录不多出任何文件")]
    public async Task 识别不写盘()
    {
        var before = Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories).ToList();
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());

        var after = Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(before, after);
        Assert.False(Directory.Exists(Path.Combine(_dir, "mods")));   // 识别阶段绝不建 mods/
    }

    [Fact(DisplayName = "识别 mrpack 整合包")]
    public async Task 识别整合包()
    {
        var pack = Zip("p.mrpack", ("modrinth.index.json",
            """{"formatVersion":1,"name":"P","versionId":"1","dependencies":{"minecraft":"26.3","fabric-loader":"0.19.5"},"files":[{"path":"mods/a.jar","env":{"client":"required","server":"required"},"downloads":["https://example.invalid/a.jar"],"size":11}]}"""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(pack);

        Assert.Equal(ImportKind.Mrpack, vm.Kind);
        Assert.Equal("Modrinth 整合包（.mrpack）", vm.KindLabel);
        Assert.True(vm.Actionable);
    }

    [Fact(DisplayName = "摘要一句话说清「是什么/几个文件/多大/写到哪」")]
    public async Task 预览摘要()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());

        Assert.Contains("Fabric 模组", vm.StatusText);
        Assert.Contains("1 个文件", vm.StatusText);
        Assert.Contains("mods/", vm.StatusText);
        Assert.Matches(@"\d+(\.\d+)? (B|KB|MB|GB)", vm.StatusText);   // 带人类可读体积
    }

    // ========== 匹配判定（保守原则）==========

    [Fact(DisplayName = "加载器匹配且版本相符 → 报匹配")]
    public async Task 匹配通过()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar("26.3"));
        Assert.Equal(MatchVerdict.Matches, vm.Match);
        Assert.Equal("与当前实例匹配 ✓", vm.MatchLabel);
    }

    [Fact(DisplayName = "客户端专用模组在 fabric 实例上判为不匹配并拦下")]
    public async Task 客户端模组不匹配()
    {
        var jar = Zip("client.jar",
            ("fabric.mod.json", """{"id":"c","version":"1","environment":"client","depends":{"minecraft":"26.3"}}"""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jar);

        Assert.Equal(MatchVerdict.Mismatch, vm.Match);
        Assert.False(vm.CanInstall);
        Assert.NotEmpty(vm.BlockedReason);
        // 拦下时必须说清原因，且告诉用户还能强行导入
        Assert.Contains("强行导入", vm.BlockedReason);
    }

    [Fact(DisplayName = "勾选强行导入后放行")]
    public async Task 强行导入放行()
    {
        var jar = Zip("client2.jar",
            ("fabric.mod.json", """{"id":"c2","version":"1","environment":"client","depends":{"minecraft":"26.3"}}"""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jar);
        Assert.False(vm.CanInstall);

        vm.AllowMismatch = true;
        Assert.True(vm.CanInstall);
    }

    [Fact(DisplayName = "版本无法确证时报「不确定」而非乐观的匹配")]
    public async Task 版本不确定()
    {
        // depends.minecraft 写范围而非精确值 → Core 判 Unknown（保守原则）
        var jar = Zip("range.jar",
            ("fabric.mod.json", """{"id":"r","version":"1","environment":"*","depends":{"minecraft":"[1.20,)"}}"""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jar);

        Assert.Equal(MatchVerdict.Unknown, vm.Match);
        Assert.Equal("无法确认是否匹配", vm.MatchLabel);
        Assert.True(vm.CanInstall);          // Unknown 不拦（拦了就没法用了），但不得报「匹配 ✓」
        Assert.DoesNotContain("✓", vm.MatchLabel);
    }

    [Fact(DisplayName = "neoforge 模组装进 fabric 实例：不匹配")]
    public async Task 加载器不符()
    {
        var jar = Zip("nf.jar", ("META-INF/neoforge.mods.toml", "modId=\"x\"\nversion=\"1\""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jar);

        Assert.Equal(ImportKind.NeoForgeMod, vm.Kind);
        Assert.Equal(MatchVerdict.Mismatch, vm.Match);
        Assert.False(vm.CanInstall);
    }

    // ========== 不可自动导入的分支（必须解释，不能只置灰）==========

    [Fact(DisplayName = "无标记 jar 判为疑似服务端 jar，拦下并解释后果")]
    public async Task 疑似服务端jar()
    {
        var jar = Zip("plain.jar", ("some/Random.class", "binary"));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jar);

        Assert.Equal(ImportKind.ServerJarCandidate, vm.Kind);
        Assert.False(vm.Actionable);
        Assert.False(vm.CanInstall);
        // 必须说清「塞进 mods/ 会启动崩溃」，否则用户只会看到按钮不可点
        Assert.Contains("mods/", vm.BlockedReason);
        Assert.Contains("崩溃", vm.BlockedReason);
        Assert.Contains("服务端", vm.BlockedReason);
    }

    [Fact(DisplayName = "MultiMC/PCL 实例包：如实说明暂不完整支持")]
    public async Task MultiMc包()
    {
        var pack = Zip("inst.zip", ("version.json", """{"formatVersion":1,"name":"x"}"""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(pack);

        Assert.Equal(ImportKind.MultiMcPack, vm.Kind);
        Assert.False(vm.Actionable);
        Assert.Contains("暂不完整支持", vm.BlockedReason);
        Assert.Contains("整合包", vm.BlockedReason);   // 给出替代路径
    }

    [Fact(DisplayName = "读不出的文件判为无法识别，不抛异常")]
    public async Task 无法识别()
    {
        var junk = Path.Combine(_files, "junk.zip");
        File.WriteAllText(junk, "这不是 zip");
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(junk);

        Assert.Equal(ImportKind.Unknown, vm.Kind);
        Assert.False(vm.CanInstall);
        Assert.False(vm.HasError);          // 「识别不出」不是错误，是要用户裁决的状态
        Assert.Contains("无法识别", vm.KindLabel);
        Assert.NotEmpty(vm.BlockedReason);
    }

    [Fact(DisplayName = "mods/ 合集压缩包可识别")]
    public async Task 模组合集()
    {
        var bundle = Zip("bundle.zip", ("mods/", ""), ("mods/a.jar", "x"));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(bundle);

        Assert.Equal(ImportKind.ModsBundle, vm.Kind);
        Assert.True(vm.Actionable);
    }

    // ========== 输入校验 ==========

    [Fact(DisplayName = "文件不存在给出明确提示")]
    public async Task 文件不存在()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(Path.Combine(_files, "没有这个.jar"));

        Assert.True(vm.HasError);
        Assert.Contains("不存在", vm.StatusText);
        Assert.False(vm.Inspected);
    }

    [Fact(DisplayName = "未选文件时提示选择")]
    public async Task 未选文件()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync("   ");

        Assert.True(vm.HasError);
        Assert.Contains("选择要导入的文件", vm.StatusText);
    }

    [Fact(DisplayName = "未识别前 BlockedReason 说明下一步，CanInstall 为假")]
    public void 未识别状态()
    {
        var vm = NewVm();
        Assert.False(vm.CanInstall);
        Assert.Contains("选择", vm.BlockedReason);
        Assert.Equal("（尚未识别）", vm.KindLabel);
    }

    [Fact(DisplayName = "扩展名过滤：只收 jar/mrpack/zip（拖拽过滤用）")]
    public void 扩展名过滤()
    {
        Assert.True(ImportViewModel.IsSupportedFile("a.jar"));
        Assert.True(ImportViewModel.IsSupportedFile("a.MRPACK"));
        Assert.True(ImportViewModel.IsSupportedFile("a.zip"));
        Assert.False(ImportViewModel.IsSupportedFile("a.txt"));
        Assert.False(ImportViewModel.IsSupportedFile("a.exe"));
        Assert.False(ImportViewModel.IsSupportedFile("无扩展名"));
    }

    // ========== 覆盖提示 ==========

    [Fact(DisplayName = "同名文件会被覆盖时提前告知并标记")]
    public async Task 覆盖提示()
    {
        var jarPath = FabricModJar();
        Directory.CreateDirectory(Path.Combine(_dir, "mods"));
        File.WriteAllText(Path.Combine(_dir, "mods", "mod.jar"), "旧版本内容");

        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jarPath);

        Assert.Equal(1, vm.OverwriteCount);
        Assert.Single(vm.Files, f => f.Overwrites);
        Assert.Contains(vm.Notes, n => n.Contains("覆盖"));
    }

    [Fact(DisplayName = "文件清单超过上限时折叠并说明剩余数量")]
    public async Task 清单折叠()
    {
        var entries = new List<(string, string)> { ("mods/", "") };
        for (var i = 0; i < 60; i++) entries.Add(($"mods/m{i}.jar", "x"));
        var bundle = Zip("big.zip", [.. entries]);

        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(bundle);

        Assert.Equal(60, vm.FileCount);
        Assert.True(vm.Files.Count < vm.FileCount, "界面不应列出全部 60 条");
        Assert.Contains(vm.Notes, n => n.Contains("未列出"));
    }

    // ========== 执行导入 ==========

    [Fact(DisplayName = "导入写入 mods/ 并把记录登记进 instance.json")]
    public async Task 导入落盘与登记()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());
        Assert.True(vm.CanInstall);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.False(vm.HasError, vm.StatusText);
        Assert.Contains("导入完成", vm.StatusText);
        Assert.True(File.Exists(Path.Combine(_dir, "mods", "mod.jar")));
        // 走 Core 的 AppendMods：与 CLI 共用一套写回逻辑，不得两处各写一份
        var meta = JsonUtil.Parse<InstanceMeta>(
            File.ReadAllText(Path.Combine(_dir, "instance.json")));
        Assert.Contains(meta.Mods, m => m.FileName == "mod.jar");
        // 导入后清空预览，避免用户以为还没导
        Assert.False(vm.Inspected);
        Assert.Empty(vm.Files);
    }

    [Fact(DisplayName = "运行中实例禁止导入（热塞 mods/ 不生效且可能撞文件锁）")]
    public async Task 运行中禁止导入()
    {
        File.WriteAllText(Path.Combine(_dir, ".state"), "RUNNING");
        File.WriteAllText(Path.Combine(_dir, ".pid"), Environment.ProcessId.ToString());

        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());
        Assert.True(vm.CanInstall);        // 预览仍可看

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("正在运行", vm.StatusText);
        Assert.Contains("停止", vm.StatusText);
        Assert.False(Directory.Exists(Path.Combine(_dir, "mods")));   // 一个文件都没写
    }

    [Fact(DisplayName = "不匹配时未勾选强行导入则拒绝执行")]
    public async Task 不匹配拒绝执行()
    {
        var jar = Zip("c3.jar",
            ("fabric.mod.json", """{"id":"c3","version":"1","environment":"client","depends":{"minecraft":"26.3"}}"""));
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(jar);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.False(Directory.Exists(Path.Combine(_dir, "mods")));
    }

    [Fact(DisplayName = "未识别就点导入会被拒绝")]
    public async Task 未识别拒绝导入()
    {
        var vm = NewVm();
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("识别", vm.StatusText);
    }

    [Fact(DisplayName = "Reset 清空全部预览状态与提示")]
    public async Task 重置()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());
        Assert.True(vm.Inspected);
        Assert.NotEmpty(vm.StatusText);

        vm.ResetCommand.Execute(null);

        Assert.False(vm.Inspected);
        Assert.Null(vm.Kind);
        Assert.Empty(vm.Files);
        Assert.Empty(vm.Evidence);
        Assert.Empty(vm.Notes);
        Assert.False(vm.CanInstall);
        Assert.Equal(0, vm.FileCount);
        Assert.Equal(0, vm.OverwriteCount);
        Assert.False(vm.AllowMismatch);
        // 用户主动清空时提示也要清：预览区空了却还留着上次的结果会自相矛盾。
        // 但 InstallAsync 内部走的是 ClearPreview（不清提示），成功消息必须留住——
        // 这两条语义由「导入落盘与登记」用例分别钉住。
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.HasError);
    }

    [Fact(DisplayName = "导入成功后状态栏仍留着完成反馈（不被内部清理抹掉）")]
    public async Task 导入后保留反馈()
    {
        // 回归守卫：InstallAsync 内部清空预览时曾连带把刚写的「导入完成」一起吃掉，
        // 用户导入成功后看不到任何反馈。故清空必须只清预览、不碰 StatusText。
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.False(vm.HasError, vm.StatusText);
        Assert.Contains("导入完成", vm.StatusText);   // 反馈仍在
        Assert.False(vm.Inspected);                   // 但预览已清（避免以为还没导）
        Assert.Empty(vm.Files);
    }

    [Fact(DisplayName = "换文件重新识别会覆盖上一次预览")]
    public async Task 换文件重新识别()
    {
        var vm = NewVm();
        await vm.InspectCommand.ExecuteAsync(FabricModJar());
        Assert.Equal(ImportKind.FabricMod, vm.Kind);

        var nf = Zip("nf2.jar", ("META-INF/neoforge.mods.toml", "modId=\"y\""));
        await vm.InspectCommand.ExecuteAsync(nf);

        Assert.Equal(ImportKind.NeoForgeMod, vm.Kind);
        Assert.DoesNotContain("fabric.mod.json", string.Join(" ", vm.Evidence));
    }

    // ========== 工具 ==========

    [Fact(DisplayName = "体积格式化")]
    public void 体积格式()
    {
        Assert.Equal("512 B", ImportViewModel.HumanSize(512));
        Assert.Equal("1.5 KB", ImportViewModel.HumanSize(1536));
        Assert.Equal("2 MB", ImportViewModel.HumanSize(2 * 1024 * 1024));
        Assert.Equal("1.5 GB", ImportViewModel.HumanSize((long)(1.5 * 1024 * 1024 * 1024)));
        Assert.Equal("0 B", ImportViewModel.HumanSize(0));
    }
}
