using Ceobe.Core.Config;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// server.properties 保序保注释读写与 schema 校验。
/// 逐条移植自 Java 版 ServerPropertiesFileTest（docs/MIGRATION.md 硬契约 1 的守卫测试）。
/// </summary>
public class ServerPropertiesFileTest
{
    /// <summary>vanilla 26.3 实测生成样本（节选关键行 + 注释 + 转义）。</summary>
    private const string Sample = """
        #Minecraft server properties
        #Thu Oct 15 11:31:44 CST 2026
        accepts-transfers=false
        allow-flight=false
        difficulty=easy
        level-type=minecraft\:normal
        max-players=20
        motd=A Minecraft Server
        online-mode=true
        server-port=25565
        view-distance=10

        """;

    private static (string Dir, string File) TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-props-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "server.properties"));
    }

    private static void WithSample(Action<string> body)
    {
        var (dir, file) = TempFile();
        try
        {
            File.WriteAllText(file, Sample, new System.Text.UTF8Encoding(false));
            body(file);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact(DisplayName = "读取语义值并还原转义")]
    public void 读取语义值并还原转义() => WithSample(file =>
    {
        var props = ServerPropertiesFile.Load(file);
        Assert.Equal("easy", props.Get("difficulty"));
        Assert.Equal("minecraft:normal", props.Get("level-type")); // 冒号转义应被还原
        Assert.Equal("A Minecraft Server", props.Get("motd"));
    });

    [Fact(DisplayName = "仅改动目标行其余字节不变")]
    public void 仅改动目标行其余字节不变() => WithSample(file =>
    {
        var props = ServerPropertiesFile.Load(file);
        props.Set("max-players", "30");
        props.Save();

        var after = File.ReadAllLines(file);
        Assert.Contains("max-players=30", after);
        Assert.Contains("#Thu Oct 15 11:31:44 CST 2026", after);   // 注释必须保留
        Assert.Contains(@"level-type=minecraft\:normal", after);    // 其他行字节不变
        Assert.Equal(Sample.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, after.Length);
    });

    [Fact(DisplayName = "新增键追加到末尾")]
    public void 新增键追加到末尾() => WithSample(file =>
    {
        var props = ServerPropertiesFile.Load(file);
        props.Set("enable-rcon", "true");
        props.Save();

        var after = File.ReadAllLines(file);
        Assert.Equal("enable-rcon=true", after[^1]);
    });

    [Fact(DisplayName = "空行与行尾风格被原样保留")]
    public void 空行与行尾风格被原样保留()
    {
        // Java Files.readAllLines 把空行也算一行；C# 侧若用 RemoveEmptyEntries 会吞掉空行，
        // 导致整文件字节漂移。此测试守卫该修正。
        var (dir, file) = TempFile();
        try
        {
            var withBlank = "motd=a\n\nmax-players=20\r\n";
            File.WriteAllText(file, withBlank);
            var props = ServerPropertiesFile.Load(file);
            props.Set("motd", "b");
            props.Save();

            var text = File.ReadAllText(file);
            Assert.Contains("\r\n", text);              // 原 CRLF 行尾被沿用
            Assert.Equal(3, File.ReadLines(file).Count()); // 空行仍在
            Assert.StartsWith("motd=b", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact(DisplayName = "schema 校验拒绝非法值")]
    public void Schema校验拒绝非法值()
    {
        var (dir, file) = TempFile();
        try
        {
            var props = ServerPropertiesFile.Load(file);
            Assert.Throws<CeobeException>(() => props.Set("view-distance", "99"));
            Assert.Throws<CeobeException>(() => props.Set("gamemode", "fly"));
            Assert.Throws<CeobeException>(() => props.Set("max-players", "abc"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact(DisplayName = "模板覆盖全部非 legacy 键")]
    public void 模板覆盖全部非Legacy键()
    {
        var template = ServerPropertiesFile.Template();
        foreach (var k in PropSchema.All().Where(k => !k.Legacy))
        {
            Assert.True(template.Contains(k.Key + "="), $"模板缺键: {k.Key}");
        }
        Assert.DoesNotContain("spawn-monsters", template); // legacy 键不应进模板
    }

    [Fact(DisplayName = "模板与读回构成幂等闭环")]
    public void 模板读回幂等()
    {
        var (dir, file) = TempFile();
        try
        {
            File.WriteAllText(file, ServerPropertiesFile.Template());
            var props = ServerPropertiesFile.Load(file);
            // 模板里 minecraft\:normal 读回必须还原成带冒号的语义值
            Assert.Equal("minecraft:normal", props.Get("level-type"));
            Assert.False(props.IsDirty);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
