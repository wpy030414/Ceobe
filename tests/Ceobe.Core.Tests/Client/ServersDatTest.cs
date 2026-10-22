using Ceobe.Core.Client;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// servers.dat 的合并语义：**不能**每次启动都重置用户的服务器列表。
/// 这三条断言对应三种真实故障：丢条目、丢 icon、地址缺端口。
/// </summary>
public class ServersDatTest
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "ceobe-servers-" + Guid.NewGuid().ToString("N")[..8] + ".dat");

    [Fact(DisplayName = "首次写入并读回")]
    public void 首次写入()
    {
        var file = TempFile();
        try
        {
            var (added, updated) = ServersDatFile.Sync(file, [
                new ServerEntry("生存重启", "a.example:25565"),
                new ServerEntry("创造建筑", "b.example:25566"),
            ]);
            Assert.Equal(2, added);
            Assert.Equal(0, updated);

            var loaded = ServersDatFile.Load(file);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("生存重启", loaded[0].Name);
            Assert.Equal("b.example:25566", loaded[1].Ip);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "再次 Sync：按 ip 认领、只更新名字、新增不重复")]
    public void 合并不重置()
    {
        var file = TempFile();
        try
        {
            ServersDatFile.Sync(file, [new ServerEntry("旧名", "a.example:25565")]);

            var (added, updated) = ServersDatFile.Sync(file, [
                new ServerEntry("新名", "a.example:25565"),
                new ServerEntry("新增", "c.example:25565"),
            ]);

            Assert.Equal(1, added);
            Assert.Equal(1, updated);
            var loaded = ServersDatFile.Load(file);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("新名", loaded[0].Name);
            Assert.Equal("新增", loaded[1].Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "合并保留我们不认识的字段（icon / acceptTextures）")]
    public void 保留未知字段()
    {
        var file = TempFile();
        try
        {
            // 模拟别的启动器写下的记录：带 icon
            var root = NbtTag.Compound().Set("servers", NbtTag.List(NbtType.Compound,
            [
                NbtTag.Compound()
                    .Set("name", NbtTag.String("朋友服"))
                    .Set("ip", NbtTag.String("d.example:25565"))
                    .Set("icon", NbtTag.String("data:image/png;base64,AAAA"))
                    .Set("acceptTextures", NbtTag.Byte(1)),
            ]));
            NbtFile.Write(file, root, gzip: false);

            ServersDatFile.Sync(file, [new ServerEntry("朋友服（改名）", "d.example:25565")]);

            var after = NbtFile.Read(file);
            var first = after["servers"]!.Items[0];
            Assert.Equal("朋友服（改名）", first["name"]?.AsString());
            Assert.Equal("data:image/png;base64,AAAA", first["icon"]?.AsString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory(DisplayName = "地址规范化：缺端口补 25565，带协议取 host:port")]
    [InlineData("survival-bj.pinggy.io", "survival-bj.pinggy.io:25565")]
    [InlineData("survival-bj.pinggy.io:25566", "survival-bj.pinggy.io:25566")]
    [InlineData("tcp://100.88.12.4:25566", "100.88.12.4:25566")]
    [InlineData("  a.example:25565  ", "a.example:25565")]
    public void 地址规范化(string input, string expected)
    {
        Assert.Equal(expected, ServersDatFile.NormalizeAddress(input));
    }
}
