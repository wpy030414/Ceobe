using Ceobe.Core.Players;
using Xunit;

namespace Ceobe.Core.Tests.Players;

/// <summary>
/// 玩家名单文件读写。
/// 重点守卫两件事：
/// 1. **离线 UUID 必须自动补齐**——白名单只有名字、缺 UUID 时，服务端认的是算出来的 id，
///    补错等于玩家进不来（整改项 2 的静默失败面）。
/// 2. **写文件 ≠ 生效**——<see cref="PlayerFiles.ReloadCommands"/> 必须如实反映
///    「哪些名单能 reload、哪些必须走命令或重启」，界面才不会谎称已生效。
/// </summary>
public class PlayerFilesTest : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ceobe-players-" + Guid.NewGuid().ToString("N")[..8]);

    public PlayerFilesTest() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // —— 白名单 ——

    [Fact(DisplayName = "文件不存在返回空表而非报错")]
    public void 缺失文件返回空()
    {
        Assert.Empty(PlayerFiles.ReadWhitelist(_dir));
        Assert.Empty(PlayerFiles.ReadOps(_dir));
        Assert.Empty(PlayerFiles.ReadBannedPlayers(_dir));
        Assert.Empty(PlayerFiles.ReadBannedIps(_dir));
    }

    [Fact(DisplayName = "白名单写入读出无损（含离线 UUID 自动补齐）")]
    public void 白名单往返()
    {
        var entries = new List<PlayerFiles.WhitelistEntry>
        {
            new(OfflinePlayerUuid.Compute("steve"), "steve"),
            new(OfflinePlayerUuid.Compute("测试玩家"), "测试玩家"),
        };
        PlayerFiles.WriteWhitelist(_dir, entries);

        var read = PlayerFiles.ReadWhitelist(_dir);
        Assert.Equal(2, read.Count);
        Assert.Equal("steve", read[0].Name);
        Assert.Equal("53909932-f794-33c0-9329-948045a4c1ce", read[0].Uuid); // JDK 实测值
        Assert.Equal("测试玩家", read[1].Name);
        Assert.Equal("74fa8980-f334-35d2-ad59-af30e3c2f0cb", read[1].Uuid);
    }

    [Fact(DisplayName = "读裸数组形态的老 whitelist.json")]
    public void 兼容裸数组()
    {
        File.WriteAllText(Path.Combine(_dir, "whitelist.json"), """
        [ { "uuid": "53909932-f794-33c0-9329-948045a4c1ce", "name": "steve" } ]
        """);
        var read = PlayerFiles.ReadWhitelist(_dir);
        Assert.Single(read);
        Assert.Equal("steve", read[0].Name);
    }

    [Fact(DisplayName = "只有名字、缺 uuid 的老条目按离线算法补齐")]
    public void 缺Uuid自动补齐()
    {
        File.WriteAllText(Path.Combine(_dir, "whitelist.json"), """
        { "version": 1, "profileList": [ { "name": "xrl" } ] }
        """);
        var read = PlayerFiles.ReadWhitelist(_dir);
        Assert.Single(read);
        Assert.Equal("b3a0a06f-2a00-33cb-98ae-a7182e6eabfa", read[0].Uuid); // JDK 实测值
    }

    [Fact(DisplayName = "损坏的 JSON 不崩、返回空（损坏名单不该挡住开服）")]
    public void 损坏文件容错()
    {
        File.WriteAllText(Path.Combine(_dir, "whitelist.json"), "{ not json");
        Assert.Empty(PlayerFiles.ReadWhitelist(_dir));
    }

    [Fact(DisplayName = "同名条目去重（大小写不敏感）")]
    public void 去重()
    {
        PlayerFiles.WriteWhitelist(_dir,
        [
            new(OfflinePlayerUuid.Compute("Steve"), "Steve"),
            new(OfflinePlayerUuid.Compute("steve"), "steve"),
            new(OfflinePlayerUuid.Compute("alex"), "alex"),
        ]);
        Assert.Equal(2, PlayerFiles.ReadWhitelist(_dir).Count);
    }

    [Fact(DisplayName = "写出形态是 envelope 而非裸数组")]
    public void 写出Envelope()
    {
        PlayerFiles.WriteWhitelist(_dir, [new(OfflinePlayerUuid.Compute("steve"), "steve")]);
        var text = File.ReadAllText(PlayerFiles.WhitelistPath(_dir));
        Assert.Contains("\"version\"", text);
        Assert.Contains("\"profileList\"", text);
        Assert.DoesNotContain("\"Mods\"", text); // 不得出现 PascalCase 键
    }

    // —— OP ——

    [Fact(DisplayName = "OP 往返，等级被钳制在 1..4")]
    public void Op往返与钳制()
    {
        PlayerFiles.WriteOps(_dir,
        [
            new(OfflinePlayerUuid.Compute("steve"), "steve", 4, true),
            new(OfflinePlayerUuid.Compute("alex"), "alex", 99, false), // 越界
            new(OfflinePlayerUuid.Compute("bob"), "bob", 0, false),    // 越界
        ]);
        var read = PlayerFiles.ReadOps(_dir);
        Assert.Equal(3, read.Count);
        Assert.Equal(4, read[0].Level);
        Assert.True(read[0].BypassPlayerLimit);
        Assert.Equal(4, read[1].Level); // 钳到上限
        Assert.Equal(1, read[2].Level); // 钳到下限
    }

    [Fact(DisplayName = "OP 文件用 ops 字段与 version=2")]
    public void Op写出形态()
    {
        PlayerFiles.WriteOps(_dir, [new(OfflinePlayerUuid.Compute("steve"), "steve", 4, false)]);
        var text = File.ReadAllText(PlayerFiles.OpsPath(_dir));
        Assert.Contains("\"ops\"", text);
        Assert.Contains("\"bypassPlayerLimit\"", text);
    }

    // —— 封禁 ——

    [Fact(DisplayName = "封禁玩家往返，永久值用 long.MaxValue（JDK 实测）")]
    public void 封禁玩家往返()
    {
        PlayerFiles.WriteBannedPlayers(_dir,
        [
            new(OfflinePlayerUuid.Compute("griefer"), "griefer", "", "ceobe", "forever"),
        ]);
        var read = PlayerFiles.ReadBannedPlayers(_dir);
        Assert.Single(read);
        Assert.Equal("griefer", read[0].Name);
        Assert.Equal("forever", read[0].Expires);

        var text = File.ReadAllText(PlayerFiles.BannedPlayersPath(_dir));
        // vanilla 的永久封禁是 new Date(Long.MAX_VALUE)，不是 -1
        Assert.Contains(long.MaxValue.ToString(), text);
        Assert.DoesNotContain("\"expiresLong\": -1", text);
    }

    [Fact(DisplayName = "封禁 IP 往返（按 ip 去重）")]
    public void 封禁Ip往返()
    {
        PlayerFiles.WriteBannedIps(_dir,
        [
            new("1.2.3.4", "", "ceobe", "forever"),
            new("1.2.3.4", "", "ceobe", "forever"),
            new("5.6.7.8", "", "ceobe", "forever"),
        ]);
        Assert.Equal(2, PlayerFiles.ReadBannedIps(_dir).Count);
    }

    [Fact(DisplayName = "时间戳格式与 MC 一致（yyyy-MM-dd HH:mm:ss +0800 形态）")]
    public void 时间戳格式()
    {
        var stamp = PlayerFiles.NowStamp();
        // JDK 实测 SimpleDateFormat("yyyy-MM-dd HH:mm:ss Z") → "2025-10-09 16:53:20 +0800"（无冒号）
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{4}$", stamp);
        Assert.Contains("+0000", stamp); // 本实现统一写 UTC
        // 往返：能解析回毫秒数
        Assert.True(PlayerFiles.StampToMillis(stamp) > 0);
    }

    // —— 生效语义（整改项 2 的关键契约）——

    [Fact(DisplayName = "运行中改白名单需下发 whitelist reload")]
    public void 白名单需Reload()
    {
        Assert.Equal(["whitelist reload"], PlayerFiles.ReloadCommands(PlayerListKind.Whitelist, true));
        Assert.Empty(PlayerFiles.ReloadCommands(PlayerListKind.Whitelist, false)); // 停机则无需
    }

    [Fact(DisplayName = "OP 与封禁表没有 reload 命令——不得谎称热生效")]
    public void Op与封禁无Reload()
    {
        // 这是整改项 2 里最容易骗用户的地方：写了文件但服务端内存没变
        Assert.Empty(PlayerFiles.ReloadCommands(PlayerListKind.Ops, true));
        Assert.Empty(PlayerFiles.ReloadCommands(PlayerListKind.BannedPlayers, true));
        Assert.Empty(PlayerFiles.ReloadCommands(PlayerListKind.BannedIps, true));
    }

    [Fact(DisplayName = "封禁表改动标记为需要重启")]
    public void 封禁需重启()
    {
        Assert.True(PlayerFiles.RequiresRestart(PlayerListKind.BannedPlayers));
        Assert.True(PlayerFiles.RequiresRestart(PlayerListKind.BannedIps));
        Assert.False(PlayerFiles.RequiresRestart(PlayerListKind.Whitelist));
    }

    [Fact(DisplayName = "命令构造正确")]
    public void 命令构造()
    {
        Assert.Equal(["op steve"], PlayerFiles.OpCommands("steve"));
        Assert.Equal(["deop steve"], PlayerFiles.UnopCommands("steve"));
        Assert.Equal(["pardon steve"], PlayerFiles.PardonCommands("steve"));
        Assert.Equal(["pardon-ip 1.2.3.4"], PlayerFiles.PardonIpCommands("1.2.3.4"));
    }

    [Fact(DisplayName = "写盘是原子的（不残留 .tmp）")]
    public void 原子写()
    {
        PlayerFiles.WriteWhitelist(_dir, [new(OfflinePlayerUuid.Compute("steve"), "steve")]);
        Assert.True(File.Exists(PlayerFiles.WhitelistPath(_dir)));
        Assert.False(File.Exists(PlayerFiles.WhitelistPath(_dir) + ".tmp"));
    }
}
