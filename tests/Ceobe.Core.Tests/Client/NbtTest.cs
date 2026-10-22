using Ceobe.Core.Client;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// NBT 读写：servers.dat 与 level.dat 共用同一实现，任何一边写坏都会让游戏
/// 静默忽略文件（不报错、只是列表空了），所以必须有往返测试兜住。
/// </summary>
public class NbtTest
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "ceobe-nbt-" + Guid.NewGuid().ToString("N")[..8] + ".dat");

    [Fact(DisplayName = "Compound 往返：标量 + 嵌套 + 列表")]
    public void Compound往返()
    {
        var file = TempFile();
        try
        {
            var root = NbtTag.Compound()
                .Set("name", NbtTag.String("中文 世界"))
                .Set("count", NbtTag.Int(42))
                .Set("big", NbtTag.Long(9_000_000_000L))
                .Set("flag", NbtTag.Byte(1))
                .Set("servers", NbtTag.List(NbtType.Compound,
                [
                    NbtTag.Compound().Set("ip", NbtTag.String("a.example:25565")),
                    NbtTag.Compound().Set("ip", NbtTag.String("b.example:25565")),
                ]));

            NbtFile.Write(file, root, gzip: true);
            var read = NbtFile.Read(file);

            Assert.Equal("中文 世界", read["name"]?.AsString());
            Assert.Equal(42, read["count"]?.AsInt());
            Assert.Equal(9_000_000_000L, read["big"]?.AsLong());
            Assert.True(read["flag"]?.AsBool());
            Assert.Equal(2, read["servers"]?.Items.Count);
            Assert.Equal("b.example:25565", read["servers"]!.Items[1]["ip"]?.AsString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "未压缩写入（servers.dat 的真实形态）")]
    public void 未压缩往返()
    {
        var file = TempFile();
        try
        {
            NbtFile.Write(file, NbtTag.Compound().Set("x", NbtTag.Int(7)), gzip: false);
            var bytes = File.ReadAllBytes(file);
            // 首字节是 TAG_Compound(10)，不是 gzip 的 0x1F
            Assert.Equal(10, bytes[0]);
            Assert.Equal(7, NbtFile.Read(file)["x"]?.AsInt());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "空列表保留元素类型，不退化成 End")]
    public void 空列表保留类型()
    {
        var file = TempFile();
        try
        {
            NbtFile.Write(file, NbtTag.Compound().Set("empty", NbtTag.List(NbtType.Compound)), gzip: false);
            var read = NbtFile.Read(file);
            Assert.Equal(NbtType.Compound, read["empty"]!.ListElementType);
            Assert.Empty(read["empty"]!.Items);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
