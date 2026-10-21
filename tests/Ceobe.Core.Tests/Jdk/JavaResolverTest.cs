using Ceobe.Core.Jdk;
using Ceobe.Core.Store;
using Xunit;

namespace Ceobe.Core.Tests.Jdk;

/// <summary>
/// 解析决策链：显式 &gt; 自管 &gt; JAVA_HOME；以及 ADR-018/失败可见化的守卫行为。
/// 通过 CEOBE_HOME 指向临时目录隔离自管仓库，并在测试内改写进程级 JAVA_HOME 环境变量，
/// 因此必须与其他 Jdk 测试串行（见 <see cref="JdkSerialTestBase"/>）。
/// </summary>
public sealed class JavaResolverTest : JdkSerialTestBase
{
    private readonly string? _savedJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");

    public override void Dispose()
    {
        Environment.SetEnvironmentVariable("JAVA_HOME", _savedJavaHome);
        base.Dispose();
    }

    /// <summary>造一个「完整但假」的 JDK 目录：release 文件声明主版本 + 假启动器。</summary>
    private static string FakeJdk(string dir, int major)
    {
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "bin", JavaEnv.JavaExe), "#fake");
        File.WriteAllText(Path.Combine(dir, "release"), $"JAVA_VERSION=\"{major}.0.1\"\n");
        return dir;
    }

    [Fact]
    public void 显式指定_版本匹配_且带启动器_即ok()
    {
        var jdk = FakeJdk(Path.Combine(Home, "manual"), 17);
        var r = JavaResolver.Resolve("1.20.4", 17, jdk);
        Assert.True(r.Ok);
        Assert.Equal(jdk, r.JavaHome);
        Assert.Equal(17, r.Actual);
        Assert.Null(r.Message);
    }

    [Fact]
    public void 显式指定_缺启动器_不ok且提示可执行下一步()
    {
        var broken = Path.Combine(Home, "stripped");
        Directory.CreateDirectory(Path.Combine(broken, "bin")); // 无 bin/java：ADR-018 残破目录
        var r = JavaResolver.Resolve("1.20.4", 17, broken);
        Assert.False(r.Ok);
        Assert.Null(r.JavaHome);
        Assert.Contains("启动器", r.Message);
        Assert.Contains("下一步", r.Message);
    }

    [Fact]
    public void 显式指定_版本不符_报实际版本()
    {
        var jdk = FakeJdk(Path.Combine(Home, "wrong"), 21);
        var r = JavaResolver.Resolve("1.20.4", 17, jdk);
        Assert.False(r.Ok);
        Assert.Equal(21, r.Actual);
        Assert.Contains("需要 Java 17", r.Message);
    }

    [Fact]
    public void 自管JDK_命中_自动选用()
    {
        var installed = FakeJdk(Path.Combine(JdkStore.Root(), "jdk-17.0.9+9"), 17);
        var r = JavaResolver.Resolve("1.20.4", 17, null);
        Assert.True(r.Ok);
        Assert.Equal(installed, r.JavaHome);
        Assert.Contains("自管 JDK", r.Message);
    }

    [Fact]
    public void 自管JDK_无匹配_且JAVA_HOME缺失_给出安装提示()
    {
        FakeJdk(Path.Combine(JdkStore.Root(), "jdk-25.0.1+8"), 25); // 装了 25 但需要 17
        Environment.SetEnvironmentVariable("JAVA_HOME", null);       // 模拟无宿主 JDK
        var r = JavaResolver.Resolve("1.20.4", 17, null);
        Assert.False(r.Ok);
        Assert.Null(r.JavaHome);
        Assert.Contains("需要 Java 17", r.Message);
        Assert.Contains("ceobe jdk install 17", r.Message);
        Assert.Contains("--java-home", r.Message);
    }

    [Fact]
    public void 完全无JDK_失败可见化()
    {
        Environment.SetEnvironmentVariable("JAVA_HOME", null);
        var r = JavaResolver.Resolve("1.21.8", 21, null);
        Assert.False(r.Ok);
        Assert.Null(r.JavaHome);
        Assert.Contains("未发现带 java 启动器的运行时", r.Message);
        Assert.Contains("ceobe 不自带 JVM", r.Message); // ADR-019：退路消失必须讲明
        Assert.Contains("下一步", r.Message);
    }

    [Fact]
    public void JAVA_HOME_指向残破目录_视同不存在()
    {
        var broken = Path.Combine(Home, "broken-host");
        Directory.CreateDirectory(Path.Combine(broken, "bin"));
        Environment.SetEnvironmentVariable("JAVA_HOME", broken);
        var r = JavaResolver.Resolve("1.21.8", 21, null);
        Assert.False(r.Ok);
        // 残破 JAVA_HOME 不得报出它的版本号（-1 = 未知）
        Assert.Equal(-1, r.Actual);
        Assert.Contains("未发现带 java 启动器的运行时", r.Message);
    }

    [Fact]
    public void 宿主JAVA_HOME_版本匹配_选用它()
    {
        var host = FakeJdk(Path.Combine(Home, "host-jdk"), 21);
        Environment.SetEnvironmentVariable("JAVA_HOME", host);
        var r = JavaResolver.Resolve("1.21.8", 21, null);
        Assert.True(r.Ok);
        Assert.Equal(host, r.JavaHome);
        Assert.Equal(21, r.Actual);
    }
}
