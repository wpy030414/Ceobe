using Ceobe.Core.Players;
using Xunit;

namespace Ceobe.Core.Tests.Players;

/// <summary>
/// 离线玩家 UUID 的权威对照测试。
///
/// 期望值由本机 JDK 25 实测生成（<c>UUID.nameUUIDFromBytes("OfflinePlayer:"+name)</c>），
/// **不是推演**：算错的 UUID 会被服务端接受但玩家仍进不来，属于最难查的静默失败，
/// 所以必须与真 JDK 逐位对齐。生成方式见 docs/MIGRATION.md 整改项 2。
/// 注意 Java 的 <c>nameUUIDFromBytes</c> 内部用的是 MD5，且置 version=3 / variant=10 位；
/// .NET 的 <c>Guid(byte[])</c> 会把前 4 字节按小端序解释，故本实现走十六进制字符串拼装。
/// </summary>
public class OfflinePlayerUuidTest
{
    /// <summary>JDK 25 实测：name | UUID | 裸 MD5。含 ASCII、空串、数字名、多字节、四字平面 emoji。</summary>
    private static readonly (string Name, string Uuid, string Md5)[] Golden =
    [
        ("steve", "53909932-f794-33c0-9329-948045a4c1ce", "53909932f79473c05329948045a4c1ce"),
        ("xrl", "b3a0a06f-2a00-33cb-98ae-a7182e6eabfa", "b3a0a06f2a0073cb98aea7182e6eabfa"),
        ("Alex", "36532b5e-c442-3dbb-a24c-c7e55d0f979a", "36532b5ec442edbb224cc7e55d0f979a"),
        ("test_1", "42dc6528-1c1d-3b09-8ed0-ab7ece5315d0", "42dc65281c1d6b094ed0ab7ece5315d0"),
        ("0", "ef66baa1-e633-3a6b-9adb-f09146bd3189", "ef66baa1e6339a6b1adbf09146bd3189"),
        ("ĀĿ", "31fe8503-3669-3af2-b2d0-33ea51f368be", "31fe85033669faf232d033ea51f368be"),
        ("测试玩家", "74fa8980-f334-35d2-ad59-af30e3c2f0cb", "74fa8980f33435d22d59af30e3c2f0cb"),
        ("玩家「帝皇」", "54e21ca3-8df8-3ec3-bea1-499f43e82ccd", "54e21ca38df81ec33ea1499f43e82ccd"),
        ("😀", "869a1aa4-b58d-34ab-bcea-41045449597a", "869a1aa4b58de4abfcea41045449597a"),
    ];

    [Theory]
    [InlineData("steve")]
    [InlineData("xrl")]
    [InlineData("Alex")]
    [InlineData("test_1")]
    [InlineData("0")]
    [InlineData("ĀĿ")]
    [InlineData("测试玩家")]
    [InlineData("玩家「帝皇」")]
    [InlineData("😀")]
    public void 与JDK实测值逐位一致(string name)
    {
        var g = Golden.First(x => x.Name == name);
        Assert.Equal(g.Uuid, OfflinePlayerUuid.Compute(name));
    }

    [Fact(DisplayName = "大小写敏感（Alex 与 alex 不同 id）")]
    public void 大小写敏感()
    {
        Assert.NotEqual(OfflinePlayerUuid.Compute("Alex"), OfflinePlayerUuid.Compute("alex"));
    }

    [Fact(DisplayName = "version=3、variant=10 位被正确置位")]
    public void VersionVariant位()
    {
        foreach (var (_, uuid, _) in Golden)
        {
            // 8-4-4-4-12：第 3 段首字符是 version，第 4 段首字符高位是 variant
            Assert.Equal('3', uuid[14]);
            Assert.Contains(uuid[19], new[] { '8', '9', 'a', 'b' });
        }
    }

    [Fact(DisplayName = "裸 MD5 与 golden 一致（定位到 MD5 层而非位处理层）")]
    public void 裸MD5一致()
    {
        foreach (var (name, _, md5) in Golden)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            Assert.Equal(md5, Convert.ToHexString(bytes).ToLowerInvariant());
        }
    }

    [Fact(DisplayName = "前缀是 OfflinePlayer: 含冒号")]
    public void 前缀正确()
    {
        Assert.Equal("OfflinePlayer:", OfflinePlayerUuid.Prefix);
    }

    [Fact(DisplayName = "null 名字按空串处理（老数据容错）")]
    public void Null名字() => Assert.Equal(OfflinePlayerUuid.Compute(""), OfflinePlayerUuid.Compute(null!));

    [Fact(DisplayName = "整数形式往返无损")]
    public void 整数形式往返()
    {
        foreach (var (_, uuid, _) in Golden)
        {
            var parts = OfflinePlayerUuid.ToIntArray(uuid);
            Assert.Equal(4, parts.Length);
            Assert.Equal(uuid, OfflinePlayerUuid.FromIntArray(parts));
        }
    }

    [Theory]
    // 标准形态
    [InlineData("53909932-f794-33c0-9329-948045a4c1ce")]
    // 无连字符：注意这是 UUID 去横杠，**不等于裸 MD5**——裸 MD5 是
    // 53909932f79473c05329948045a4c1ce，version/variant 位未置（33c0 vs 73c0）
    [InlineData("53909932f79433c09329948045a4c1ce")]
    [InlineData("53909932-F794-33C0-9329-948045A4C1CE")]
    [InlineData("  53909932-f794-33c0-9329-948045a4c1ce  ")]
    public void 规范化各种书写形态(string raw) =>
        Assert.Equal("53909932-f794-33c0-9329-948045a4c1ce", OfflinePlayerUuid.Normalize(raw));

    [Fact(DisplayName = "裸 MD5 与去横杠 UUID 不同（version/variant 位已置）")]
    public void 裸MD5不同于UUID()
    {
        // 这条守卫我自己踩过的坑：别把 MD5 当 UUID 喂进 Normalize
        Assert.NotEqual("53909932f79473c05329948045a4c1ce",
                        OfflinePlayerUuid.Compute("steve").Replace("-", ""));
        Assert.Equal("53909932f79433c09329948045a4c1ce",
                     OfflinePlayerUuid.Compute("steve").Replace("-", ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uuid")]
    public void 非法输入返回Null而非抛异常(string? raw) =>
        Assert.Null(OfflinePlayerUuid.Normalize(raw));
}
