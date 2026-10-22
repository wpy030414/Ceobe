using Ceobe.App.ViewModels;
using Ceobe.Core.Model;
using Ceobe.Core.Players;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「玩家」页签 ViewModel 验收（整改项 2）。
///
/// 训练员原话：「白名单默认开启，但没有入口填写白名单，只能在控制台输入指令，
/// 建议给玩家页面单开一页」。这里守住三条最容易静默失败的语义：
///
/// 1. **写文件 ≠ 生效**：服务端在内存里缓存名单。whitelist 有 reload，
///    ops/封禁表没有。界面绝不能谎称已生效（<see cref="PlayerListViewModel.StatusText"/>）。
/// 2. **离线 UUID 必须正确**：算错的 UUID 会被服务端接受但玩家永远进不来。
///    期望值来自本机 JDK 25 实测（<see cref="OfflinePlayerUuidTest"/>）。
/// 3. **正版模式查不到 UUID 时不得写空串**：空串同样导致「文件被接受但玩家进不来」。
/// </summary>
[Collection("CeobeHome serial")]
public class PlayerListViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-players-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public PlayerListViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        _dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = _instance, ServerType = ServerType.PAPER, GameVersion = "26.3", Port = 25565,
        }));
        WriteProps(onlineMode: false, whiteList: true);
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    private void WriteProps(bool onlineMode, bool whiteList, bool enforce = false)
    {
        File.WriteAllText(Path.Combine(_dir, "server.properties"),
            $"online-mode={(onlineMode ? "true" : "false")}\n" +
            $"white-list={(whiteList ? "true" : "false")}\n" +
            $"enforce-whitelist={(enforce ? "true" : "false")}\n" +
            "server-port=25565\n");
    }

    private PlayerListViewModel NewVm()
    {
        var vm = new PlayerListViewModel(new InstanceManager(new FakeLocator()), _instance);
        vm.Load();
        return vm;
    }

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    // ========== 加载与开关 ==========

    [Fact(DisplayName = "加载白名单/OP/封禁三张表")]
    public void 加载名单()
    {
        PlayerFiles.WriteWhitelist(_dir,
        [
            new(OfflinePlayerUuid.Compute("steve"), "steve"),
            new(OfflinePlayerUuid.Compute("alex"), "alex"),
        ]);
        PlayerFiles.WriteOps(_dir, [new(OfflinePlayerUuid.Compute("steve"), "steve", 4, false)]);
        PlayerFiles.WriteBannedIps(_dir, [new("1.2.3.4", PlayerFiles.NowStamp(), "Ceobe", "forever")]);

        var vm = NewVm();
        Assert.Equal(2, vm.Whitelist.Count);
        Assert.Equal(["steve", "alex"], vm.Whitelist.Select(w => w.Name));
        Assert.Single(vm.Ops);
        Assert.Equal(4, vm.Ops[0].Level);
        Assert.Single(vm.BannedIps);
        Assert.Empty(vm.BannedPlayers);
    }

    [Fact(DisplayName = "开关状态从 server.properties 读回")]
    public void 读回开关()
    {
        WriteProps(onlineMode: true, whiteList: true, enforce: true);
        var vm = NewVm();
        Assert.True(vm.WhitelistEnabled);
        Assert.True(vm.EnforceWhitelist);
        Assert.True(vm.OnlineMode);
        Assert.False(vm.ServerRunning);
        Assert.False(vm.ControlAvailable);
    }

    [Fact(DisplayName = "白名单开着但为空时显式警告（训练员的原始痛点）")]
    public void 空白名单警告()
    {
        var vm = NewVm();
        Assert.True(vm.WhitelistEnabled);
        Assert.Empty(vm.Whitelist);
        Assert.Contains("没有人能进服", vm.StatusText);
    }

    [Fact(DisplayName = "关闭白名单后提示改为任何人可进")]
    public void 关闭白名单提示()
    {
        WriteProps(onlineMode: false, whiteList: false);
        var vm = NewVm();
        Assert.Contains("任何玩家都能进入", vm.StatusText);
    }

    [Fact(DisplayName = "离线模式下提示 UUID 按名本地算出")]
    public void 离线模式提示()
    {
        WriteProps(onlineMode: false, whiteList: false);
        var vm = NewVm();
        Assert.Contains("离线模式", vm.StatusText);
    }

    // ========== 添加白名单（离线 UUID 正确性）==========

    [Fact(DisplayName = "离线模式添加：UUID 与 JDK 实测值一致")]
    public async Task 离线UUID正确()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        var saved = PlayerFiles.ReadWhitelist(_dir);
        var entry = Assert.Single(saved);
        Assert.Equal("steve", entry.Name);
        // 本机 JDK 25 实测：UUID.nameUUIDFromBytes("OfflinePlayer:steve")
        Assert.Equal("53909932-f794-33c0-9329-948045a4c1ce", entry.Uuid);
        Assert.Single(vm.Whitelist);
    }

    [Fact(DisplayName = "中文名离线 UUID 正确（多字节 UTF-8）")]
    public async Task 中文名UUID()
    {
        var vm = NewVm();
        vm.NewPlayerName = "测试玩家";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        var entry = Assert.Single(PlayerFiles.ReadWhitelist(_dir));
        Assert.Equal("74fa8980-f334-35d2-ad59-af30e3c2f0cb", entry.Uuid);
    }

    [Fact(DisplayName = "停机时写入文件并如实提示「启动后生效」")]
    public async Task 停机提示()
    {
        var vm = NewVm();
        Assert.False(vm.ServerRunning);
        vm.NewPlayerName = "alex";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        Assert.Contains("服务端未运行", vm.StatusText);
        Assert.Contains("启动后生效", vm.StatusText);
        Assert.False(vm.HasError);
    }

    [Fact(DisplayName = "重复添加被拒且不写坏文件")]
    public async Task 重复添加()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        vm.NewPlayerName = "STEVE";   // 大小写不同也视为重复
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        Assert.Single(vm.Whitelist);
        Assert.True(vm.HasError);
        Assert.Contains("已在白名单中", vm.StatusText);
    }

    [Fact(DisplayName = "空名字被拒")]
    public async Task 空名字()
    {
        var vm = NewVm();
        vm.NewPlayerName = "   ";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Empty(vm.Whitelist);
    }

    [Fact(DisplayName = "移除白名单成员")]
    public async Task 移除成员()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);
        Assert.Single(vm.Whitelist);

        vm.RemoveFromWhitelistCommand.Execute("steve");
        Assert.Empty(vm.Whitelist);
        Assert.Empty(PlayerFiles.ReadWhitelist(_dir));
    }

    // ========== 正版模式：不得写空 UUID ==========

    [Fact(DisplayName = "正版模式查不到 UUID 时拒绝写入（不得写空串）")]
    public async Task 正版查不到UUID()
    {
        // 正版模式 + 一个 Mojang 不可能存在的名字 → 必须报错而非写入空 UUID。
        // 空 UUID 会让服务端接受文件但玩家永远匹配不上，是最难查的静默失败。
        WriteProps(onlineMode: true, whiteList: false);
        var vm = NewVm();
        Assert.True(vm.OnlineMode);

        vm.NewPlayerName = "zz";   // 短于 3 字符，MojangUuid 直接判定非法、不发请求
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("查不到", vm.StatusText);
        Assert.Empty(PlayerFiles.ReadWhitelist(_dir));   // 没有写入任何脏数据
        Assert.Empty(vm.Whitelist);
    }

    [Fact(DisplayName = "正版模式下白名单里不得存在空 UUID 条目")]
    public async Task 无空UUID条目()
    {
        WriteProps(onlineMode: true, whiteList: true);
        var vm = NewVm();
        vm.NewPlayerName = "nope_not_a_real_name_xyz";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);

        var saved = PlayerFiles.ReadWhitelist(_dir);
        Assert.All(saved, e => Assert.False(string.IsNullOrWhiteSpace(e.Uuid)));
    }

    // ========== 批量导入 ==========

    [Fact(DisplayName = "批量导入去重并跳过已存在的")]
    public async Task 批量导入()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);   // 磁盘已有 steve

        await vm.ImportWhitelistCommand.ExecuteAsync("steve, alex, bob");

        // 磁盘共 steve+alex+bob 三人（steve 被导入去重跳过），本次新增 alex/bob 两人
        Assert.Equal(3, vm.Whitelist.Count);
        Assert.Contains("导入 2 人", vm.StatusText);
        Assert.Contains("跳过 1 个重复", vm.StatusText);
        Assert.All(PlayerFiles.ReadWhitelist(_dir), e => Assert.NotEqual("", e.Uuid));
    }

    [Fact(DisplayName = "批量导入解析多种分隔符与空项")]
    public async Task 分隔符解析()
    {
        var vm = NewVm();
        await vm.ImportWhitelistCommand.ExecuteAsync("a1\nb2; c3,,\td4 ");
        Assert.Equal(4, vm.Whitelist.Count);
    }

    [Fact(DisplayName = "批量导入无有效名字时报错")]
    public async Task 批量导入空输入()
    {
        var vm = NewVm();
        await vm.ImportWhitelistCommand.ExecuteAsync("  , , ");
        Assert.True(vm.HasError);
        Assert.Contains("没有解析到", vm.StatusText);
    }

    [Fact(DisplayName = "从 OP 导入白名单")]
    public async Task 从OP导入()
    {
        PlayerFiles.WriteOps(_dir,
        [
            new(OfflinePlayerUuid.Compute("steve"), "steve", 4, false),
            new(OfflinePlayerUuid.Compute("alex"), "alex", 2, false),
        ]);
        var vm = NewVm();
        await vm.ImportFromOpsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Whitelist.Count);
        Assert.Equal(["steve", "alex"], vm.Whitelist.Select(w => w.Name));
    }

    // ========== OP ==========

    [Fact(DisplayName = "停机时加 OP 写入文件，等级钳制在 1..4")]
    public async Task 加OP()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        vm.NewOpLevel = 99;
        await vm.AddOpCommand.ExecuteAsync(null);

        var op = Assert.Single(PlayerFiles.ReadOps(_dir));
        Assert.Equal("steve", op.Name);
        Assert.Equal(4, op.Level);                       // 钳到上限
        Assert.Equal("53909932-f794-33c0-9329-948045a4c1ce", op.Uuid);
    }

    [Fact(DisplayName = "OP 无 reload 命令：停机提示启动后生效，不谎称已热生效")]
    public async Task OP生效语义()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        await vm.AddOpCommand.ExecuteAsync(null);

        // ops.json 没有 reload：绝不能出现「已即时生效」这类措辞
        Assert.DoesNotContain("即时生效", vm.StatusText);
        Assert.Contains("服务端未运行", vm.StatusText);
    }

    [Fact(DisplayName = "重复加 OP 覆盖旧等级而非追加重复条目")]
    public async Task OP去重()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        vm.NewOpLevel = 2;
        await vm.AddOpCommand.ExecuteAsync(null);
        vm.NewPlayerName = "steve";
        vm.NewOpLevel = 4;
        await vm.AddOpCommand.ExecuteAsync(null);

        var ops = PlayerFiles.ReadOps(_dir);
        Assert.Single(ops);
        Assert.Equal(4, ops[0].Level);
    }

    [Fact(DisplayName = "移除 OP")]
    public async Task 移除OP()
    {
        var vm = NewVm();
        vm.NewPlayerName = "steve";
        await vm.AddOpCommand.ExecuteAsync(null);
        vm.RemoveOpCommand.Execute("steve");
        Assert.Empty(PlayerFiles.ReadOps(_dir));
    }

    // ========== 封禁 ==========

    [Fact(DisplayName = "封禁玩家写入永久值与正确 UUID")]
    public async Task 封禁玩家()
    {
        var vm = NewVm();
        vm.NewPlayerName = "griefer";
        await vm.BanPlayerCommand.ExecuteAsync(null);

        var ban = Assert.Single(PlayerFiles.ReadBannedPlayers(_dir));
        Assert.Equal("griefer", ban.Name);
        Assert.Equal("forever", ban.Expires);
        Assert.NotEqual("", ban.Uuid);
        Assert.Equal("Ceobe", ban.Source);
    }

    [Fact(DisplayName = "封禁 IP 去重")]
    public void 封禁IP()
    {
        var vm = NewVm();
        vm.NewIp = "1.2.3.4";
        vm.BanIpCommand.Execute(null);
        vm.NewIp = "1.2.3.4";
        vm.BanIpCommand.Execute(null);

        Assert.Single(PlayerFiles.ReadBannedIps(_dir));
        Assert.Single(vm.BannedIps);
    }

    [Fact(DisplayName = "封禁 IP 空输入被拒")]
    public void 封禁IP空输入()
    {
        var vm = NewVm();
        vm.NewIp = "  ";
        vm.BanIpCommand.Execute(null);
        Assert.True(vm.HasError);
        Assert.Empty(PlayerFiles.ReadBannedIps(_dir));
    }

    [Fact(DisplayName = "解封玩家与 IP")]
    public async Task 解封()
    {
        var vm = NewVm();
        vm.NewPlayerName = "griefer";
        await vm.BanPlayerCommand.ExecuteAsync(null);
        vm.NewIp = "9.9.9.9";
        vm.BanIpCommand.Execute(null);

        vm.PardonPlayerCommand.Execute("griefer");
        vm.PardonIpCommand.Execute("9.9.9.9");

        Assert.Empty(PlayerFiles.ReadBannedPlayers(_dir));
        Assert.Empty(PlayerFiles.ReadBannedIps(_dir));
    }

    // ========== 开关写回 ==========

    [Fact(DisplayName = "切换白名单开关写回 properties 且保留其余字节")]
    public void 切换白名单开关()
    {
        WriteProps(onlineMode: false, whiteList: true);
        // 加一行注释与一个带转义的键，验证写回不破坏它们
        File.AppendAllText(Path.Combine(_dir, "server.properties"),
            "# 我的注释\nlevel-type=minecraft\\:normal\n");

        var vm = NewVm();
        Assert.True(vm.WhitelistEnabled);
        vm.WhitelistEnabled = false;
        vm.ToggleWhitelistCommand.Execute(null);

        var text = File.ReadAllText(Path.Combine(_dir, "server.properties"));
        Assert.Contains("white-list=false", text);
        Assert.Contains("# 我的注释", text);
        Assert.Contains(@"level-type=minecraft\:normal", text);
        // 需重启项不得谎称热生效
        Assert.DoesNotContain("即时生效", vm.StatusText);
    }

    [Fact(DisplayName = "切换强制白名单")]
    public void 切换强制白名单()
    {
        var vm = NewVm();
        vm.EnforceWhitelist = true;
        vm.ToggleEnforceWhitelistCommand.Execute(null);

        var props = Ceobe.Core.Config.ServerPropertiesFile.Load(Path.Combine(_dir, "server.properties"));
        Assert.Equal("true", props.Get("enforce-whitelist"));
        Assert.Contains("强制白名单", vm.StatusText);
    }

    [Fact(DisplayName = "无 server.properties 时切开关给出引导而非崩溃")]
    public void 无配置文件切开关()
    {
        File.Delete(Path.Combine(_dir, "server.properties"));
        var vm = new PlayerListViewModel(new InstanceManager(new FakeLocator()), _instance);
        vm.Load();
        vm.WhitelistEnabled = true;
        vm.ToggleWhitelistCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Contains("尚无 server.properties", vm.StatusText);
    }

    // ========== 错误态生命周期（ReportApplied 的追加/保留语义）==========

    [Fact(DisplayName = "一次失败后的成功操作应清除错误态（不得一直红着）")]
    public async Task 成功操作清错误态()
    {
        var vm = NewVm();
        // 先制造一次失败：空名字
        vm.NewPlayerName = "  ";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        // 再来一次成功的：错误态必须被清除，否则界面会因早先的失败一直标红
        vm.NewPlayerName = "steve";
        await vm.AddToWhitelistCommand.ExecuteAsync(null);
        Assert.False(vm.HasError);
        Assert.Contains("服务端未运行", vm.StatusText);   // 生效说明仍在
    }

    [Fact(DisplayName = "正版模式下全部名字解析失败时不写入任何脏数据")]
    public async Task 批量全部失败()
    {
        // 两个名字都越界（zz 短于 3、also_not_real_xyz 长于 16），
        // MojangUuid 直接判非法、不发网络请求，故本用例保持离线。
        WriteProps(onlineMode: true, whiteList: false);
        var vm = NewVm();
        Assert.True(vm.OnlineMode);

        await vm.ImportWhitelistCommand.ExecuteAsync("zz, also_not_real_xyz");

        Assert.True(vm.HasError);                          // 必须标红
        Assert.Contains("查不到正版 UUID", vm.StatusText);
        Assert.Contains("0 人", vm.StatusText);            // 如实说明一个都没导入
        // 绝不写占位 UUID：空串会让服务端接受文件但玩家永远匹配不上
        Assert.Empty(PlayerFiles.ReadWhitelist(_dir));
        Assert.Empty(vm.Whitelist);
    }

    [Fact(DisplayName = "正版模式下失败者被逐个点名，不含糊带过")]
    public async Task 批量失败逐个点名()
    {
        // 只能用越界名字（zz 短于 3、also_not_real_xyz 长于 16）：
        // 合法长度的名字会让 MojangUuid 真发网络请求，那就不是离线用例了。
        // 「一次成功一次失败」的部分成功场景需要真实 Mojang 命中，属 network 用例范畴。
        WriteProps(onlineMode: true, whiteList: false);
        var vm = NewVm();
        await vm.ImportWhitelistCommand.ExecuteAsync("zz, also_not_real_xyz");

        Assert.True(vm.HasError);
        Assert.Contains("zz", vm.StatusText);                      // 失败者逐个点名
        Assert.Contains("also_not_real_xyz", vm.StatusText);
        Assert.Contains("2 人查不到正版 UUID", vm.StatusText);
        Assert.Empty(PlayerFiles.ReadWhitelist(_dir));
    }

    [Fact(DisplayName = "离线模式批量导入全部成功且不留错误态")]
    public async Task 离线批量成功()
    {
        var vm = NewVm();
        Assert.False(vm.OnlineMode);
        await vm.ImportWhitelistCommand.ExecuteAsync("steve, alex, 测试玩家");

        Assert.False(vm.HasError);
        Assert.Equal(3, vm.Whitelist.Count);
        Assert.All(PlayerFiles.ReadWhitelist(_dir), e => Assert.NotEqual("", e.Uuid));
        Assert.Contains("导入 3 人", vm.StatusText);
        Assert.Contains("已写入文件", vm.StatusText);
    }

    [Fact(DisplayName = "批量导入成功时统计与生效说明并存（追加而非覆盖）")]
    public async Task 批量成功文案并存()
    {
        var vm = NewVm();
        await vm.ImportWhitelistCommand.ExecuteAsync("steve, alex");

        Assert.False(vm.HasError);
        Assert.Contains("导入 2 人", vm.StatusText);           // 调用方统计
        Assert.Contains("已写入文件", vm.StatusText);          // ReportApplied 追加的生效说明
        Assert.Equal(2, vm.Whitelist.Count);
    }

    // ========== 损坏文件容错 ==========

    [Fact(DisplayName = "名单文件损坏时不崩，给出可读状态")]
    public void 名单损坏容错()
    {
        File.WriteAllText(PlayerFiles.WhitelistPath(_dir), "{ broken");
        var vm = NewVm();
        Assert.Empty(vm.Whitelist);      // 读不出就当空，不能抛
        Assert.False(vm.HasError);
    }
}
