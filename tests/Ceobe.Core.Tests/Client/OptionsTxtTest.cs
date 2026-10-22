using Ceobe.Core.Client;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// options.txt 的保序约束。这是「不得重写的硬契约」的客户端版本：
/// 游戏与用户都往这个文件写，整文件重写会丢顺序（资源包优先级）与未知键。
/// </summary>
public class OptionsTxtTest
{
    private static string TempFile(string content)
    {
        var f = Path.Combine(Path.GetTempPath(), "ceobe-options-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(f, content);
        return f;
    }

    [Fact(DisplayName = "改一个键：其余行、顺序、注释原样保留")]
    public void 保序改写()
    {
        var file = TempFile("""
            # 用户手写的注释
            version:3465
            fov:0.5
            resourcePacks:["vanilla","file/A.zip"]
            lang:zh_cn
            """);
        try
        {
            var options = OptionsTxt.Load(file);
            options.SetArray("resourcePacks", ["vanilla", "file/B.zip", "file/A.zip"]);
            options.Save();

            var lines = File.ReadAllLines(file);
            Assert.Equal("# 用户手写的注释", lines[0]);
            Assert.Equal("version:3465", lines[1]);
            Assert.Equal("fov:0.5", lines[2]);
            Assert.Equal("resourcePacks:[\"vanilla\",\"file/B.zip\",\"file/A.zip\"]", lines[3]);
            Assert.Equal("lang:zh_cn", lines[4]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "不存在的键追加到末尾，已有键不重复")]
    public void 追加新键()
    {
        var file = TempFile("a:1\n");
        try
        {
            var options = OptionsTxt.Load(file);
            options.Set("b", "2");
            options.Set("a", "9");
            options.Save();

            var lines = File.ReadAllLines(file);
            Assert.Equal(["a:9", "b:2"], lines);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory(DisplayName = "数组解析容错：带空格 / 缺引号也能读")]
    [InlineData("[\"a\",\"b\"]", new[] { "a", "b" })]
    [InlineData("[ \"a\" , \"b\" ]", new[] { "a", "b" })]
    [InlineData("[a,b]", new[] { "a", "b" })]
    [InlineData("[]", new string[0])]
    public void 数组解析(string raw, string[] expected)
    {
        Assert.Equal(expected, OptionsTxt.ParseArray(raw));
    }

    [Fact(DisplayName = "数组写回格式与游戏一致")]
    public void 数组写回()
    {
        Assert.Equal("[\"vanilla\",\"file/a.zip\"]",
            OptionsTxt.FormatArray(["vanilla", "file/a.zip"]));
    }
}
