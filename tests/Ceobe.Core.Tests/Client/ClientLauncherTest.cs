using Ceobe.Core.Client;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// 启动参数拼装中最容易静默出错的三处：占位符、引号切分、maven 路径。
/// 它们错了不会抛异常，只是游戏起不来或找不到库——所以逐条锁住。
/// </summary>
public class ClientLauncherTest
{
    [Fact(DisplayName = "占位符替换；未知占位符原样保留")]
    public void 占位符替换()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = "Steve",
            ["natives_directory"] = @"C:\n",
        };

        Assert.Equal("--username Steve", ClientLauncher.Substitute("--username ${auth_player_name}", vars));
        Assert.Equal("-Djava.library.path=C:\\n",
            ClientLauncher.Substitute("-Djava.library.path=${natives_directory}", vars));
        // 未知键保留原文：静默清空会让命令行悄悄少一段
        Assert.Equal("${unknown_thing}", ClientLauncher.Substitute("${unknown_thing}", vars));
        Assert.Equal("a-b", ClientLauncher.Substitute("a-${missing}b", vars).Replace("${missing}", ""));
    }

    [Theory(DisplayName = "参数串切分：引号内空格算一个参数")]
    [InlineData("-XX:+UseG1GC -Xmx4G", new[] { "-XX:+UseG1GC", "-Xmx4G" })]
    [InlineData("--width 1920 --height 1080", new[] { "--width", "1920", "--height", "1080" })]
    [InlineData("\"-Dfoo=a b\" -Xms1G", new[] { "-Dfoo=a b", "-Xms1G" })]
    [InlineData("   ", new string[0])]
    public void 参数切分(string raw, string[] expected)
    {
        Assert.Equal(expected, ClientLauncher.SplitArgs(raw));
    }

    [Theory(DisplayName = "服务器地址拆分")]
    [InlineData("a.example:25566", "a.example", 25566)]
    [InlineData("a.example", "a.example", 25565)]
    public void 地址拆分(string input, string host, int port)
    {
        var (h, p) = ClientLauncher.SplitAddress(input);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Fact(DisplayName = "maven 坐标 → 仓库路径（含 classifier 与 @ext）")]
    public void Maven路径()
    {
        Assert.Equal("net/fabricmc/fabric-loader/0.15.11/fabric-loader-0.15.11.jar",
            ClientVersion.MavenPath("net.fabricmc:fabric-loader:0.15.11", null));
        Assert.Equal("org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
            ClientVersion.MavenPath("org.lwjgl:lwjgl:3.3.1", "natives-windows"));
        Assert.Equal("com/example/x/1.0/x-1.0.zip",
            ClientVersion.MavenPath("com.example:x:1.0@zip", null));
    }

    [Fact(DisplayName = "加载器版本 id 与安装写盘一致")]
    public void 加载器版本id()
    {
        var meta = new ClientMeta { Name = "a", GameVersion = "1.20.1", Loader = ClientLoader.FABRIC, LoaderVersion = "0.15.11" };
        Assert.Equal("1.20.1-fabric0.15.11", ClientVersion.LoaderVersionId(meta));
    }
}
