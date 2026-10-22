using Ceobe.Core.Client;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// 崩溃解析是**启发式**，测试锁定的是「结论方向」而不是逐字文案：
/// 缺前置必须点出缺的 id、OOM 必须建议加内存、无报告必须说清楚为什么没有报告。
/// 文案可以改，但这些判断错了就会把用户引向删掉无辜的 Mod。
/// </summary>
public class CrashReportParserTest
{
    private const string MissingDep = """
        ---- Minecraft Crash Report ----
        // Shall we play a game?

        Time: 2026-10-22 10:26:41
        Description: Initializing game

        java.lang.NoClassDefFoundError: com/jozufozu/flywheel/api/KineticBlockEntity
        	at com.simibubi.create.Create.<clinit>(Create.java:41)
        	at net.minecraft.client.main.Main.main(Main.java:220)

        -- Mod create --
        Details:
        	Failed to load mod create

        -- System Details --
        Minecraft Version: 1.20.1
        """;

    private const string OutOfMemory = """
        ---- Minecraft Crash Report ----
        Description: Rendering overlay
        java.lang.OutOfMemoryError: Java heap space
        	at java.util.Arrays.copyOf(Arrays.java:3537)
        """;

    private const string DependencyResolution = """
        ---- Minecraft Crash Report ----
        Description: Loading Minecraft
        net.fabricmc.loader.impl.FormattedException: Mod resolution encountered an incompatible mod set!
        Missing or unsupported mandatory dependencies:
        	Mod ID: 'flywheel', Requested by: 'create', Expected range: '[0.6,)', Actual version: '[MISSING]'
        	Mod ID: 'indium', Requested by: 'sodium-extra', Expected range: '*', Actual version: '[MISSING]'
        """;

    [Fact(DisplayName = "缺前置：点名 create，并让用户装 flywheel")]
    public void 缺前置()
    {
        var a = CrashReportParser.Parse(MissingDep);
        Assert.True(a.Parsed);
        Assert.Equal("create", a.SuspectMod);
        Assert.Contains("flywheel", a.Cause);
        Assert.Contains(a.Actions, x => x.Contains("flywheel"));
        Assert.Contains("Initializing game", a.Headline);
        Assert.Contains("NoClassDefFoundError", a.ExceptionLine);
    }

    [Fact(DisplayName = "依赖解析失败：列出两个缺失 id，动作从安装开始")]
    public void 依赖解析失败()
    {
        var a = CrashReportParser.Parse(DependencyResolution);
        Assert.Contains("flywheel", a.Cause);
        Assert.Contains("indium", a.Actions.Concat([a.Cause]).Aggregate("", (s, x) => s + x));
        Assert.Contains(a.Actions, x => x.StartsWith("安装 "));
    }

    [Fact(DisplayName = "OOM：建议提高内存，而不是让用户删 Mod")]
    public void 内存不足()
    {
        var a = CrashReportParser.Parse(OutOfMemory);
        Assert.Contains("堆内存", a.Cause);
        Assert.Contains(a.Actions, x => x.Contains("MB"));
    }

    [Fact(DisplayName = "没有报告：说明是 JVM 层问题，不硬编原因")]
    public void 无报告()
    {
        var a = CrashReportParser.Parse(null);
        Assert.False(a.Parsed);
        Assert.Contains("JVM", a.Cause);
        Assert.NotEmpty(a.Actions);
    }

    [Fact(DisplayName = "平台 Mod（minecraft/forge）不作为嫌疑人")]
    public void 排除平台段落()
    {
        var text = """
            ---- Minecraft Crash Report ----
            Description: Loading
            -- Mod minecraft --
            Details:
            -- Mod flywheel --
            """;
        var a = CrashReportParser.Parse(text);
        Assert.DoesNotContain("minecraft", a.Suspects);
        Assert.Contains("flywheel", a.Suspects);
    }
}
