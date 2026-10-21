using Ceobe.Core.Model;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Store;

/// <summary>
/// instance.json / settings.json 的 wire 兼容性守卫（docs/MIGRATION.md **闸门 B**）。
///
/// fixture 由 <c>tools/wiregen/Gen.java</c> 用**真实 Jackson 2.18.11 + 真实 Java 模型类**生成，
/// 不是手打样本。这一点是本测试存在的全部理由：手打的「Java 输出」只能验证我自己的想象——
/// 字段名写错、缩进形态猜错、日期位数记错，测试照样全绿，而真实双跑会静默丢数据。
/// Java 侧模型字段变更后需重跑生成器（命令见 Gen.java 头注释）。
///
/// 反向验证（Java 能否读 C# 写的）由 <c>tools/wiregen/RoundTrip.java</c> 在 JVM 侧完成，
/// 实测结论：instance.json 三种形态全通过；settings.json 需 Java 侧
/// <c>@JsonIgnoreProperties(ignoreUnknown = true)</c> 才能容忍 C# 新增的字段。
/// </summary>
public class WireCompatibilityTest
{
    private static string Fixture(string name)
    {
        // 测试运行目录 → fixtures 子目录（csproj 里 CopyToOutputDirectory=PreserveNewest）
        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, "fixtures", name);
        Assert.True(File.Exists(path), $"缺少 fixture: {path}（需先运行 tools/wiregen/Gen.java）");
        return File.ReadAllText(path);
    }

    [Fact(DisplayName = "能读 Java 版真实写出的 instance.json（逐字段）")]
    public void 读Java产物()
    {
        var meta = JsonUtil.Parse<InstanceMeta>(Fixture("instance-full.json"));

        Assert.Equal("alpha", meta.Name);
        Assert.Equal(ServerType.FABRIC, meta.ServerType);
        Assert.Equal("26.3", meta.GameVersion);
        Assert.Equal("0.16.9", meta.LoaderVersion);
        Assert.Equal(4096, meta.MemoryMb);
        Assert.Equal("-XX:+UseG1GC", meta.JvmArgs);
        Assert.Equal(25570, meta.Port);
        Assert.Equal(@"C:\jdk-25", meta.JavaHome);
        Assert.Equal("server.jar", meta.LaunchJar);
        Assert.Null(meta.LaunchArgsFile);
        Assert.Null(meta.AuthlibInjectorUrl);
        Assert.Equal("tcp://x.run.pinggy-free.link:35663", meta.LastPinggyAddress);
        Assert.Equal("https://abc-def-ghi.trycloudflare.com", meta.LastCloudflareUrl);

        // 时间戳：Jackson 省略尾随零（.123 而非 .1230000），必须被正确吸收
        Assert.Equal(2026, meta.CreatedAt.Year);
        Assert.Equal(10, meta.CreatedAt.Month);
        Assert.Equal(15, meta.CreatedAt.Day);
        Assert.Equal(3, meta.CreatedAt.Hour);
        Assert.Equal(44, meta.CreatedAt.Second);
        Assert.Equal(123, meta.CreatedAt.Millisecond);

        // mod 清单：丢字段是静默的，必须显式断言
        var mod = Assert.Single(meta.Mods);
        Assert.Equal("PpRxcduo", mod.ProjectId);
        Assert.Equal("fabric-api", mod.Slug);
        Assert.Equal("Fabric API", mod.Title);
        Assert.Equal("0.98.0+1.21.4", mod.VersionNumber);
        Assert.Equal("fabric-api-0.98.0.jar", mod.FileName);
        Assert.Equal("required", mod.EnvServer);
        Assert.Equal("required", mod.EnvClient);
    }

    [Fact(DisplayName = "NeoForge 形态：launchArgsFile 不得丢（丢了会用 -jar 启动，起不来）")]
    public void 读NeoForge产物()
    {
        var meta = JsonUtil.Parse<InstanceMeta>(Fixture("instance-neoforge.json"));

        Assert.Equal(ServerType.NEOFORGE, meta.ServerType);
        Assert.Equal("libraries/net/neoforged/neoforge/21.4.1/win_args.txt", meta.LaunchArgsFile);
        // ADR-010：NeoForge 的 launchJar 与 launchArgsFile 同值
        Assert.Equal(meta.LaunchArgsFile, meta.LaunchJar);
        // ADR-015：设了 authlib 就自动关正版验证
        Assert.Equal("https://auth.example.test/yggdrasil", meta.AuthlibInjectorUrl);
    }

    [Fact(DisplayName = "老版本 instance.json 缺字段时宽容读回默认值")]
    public void 读最小产物()
    {
        // Java 版写的最小形态：loaderVersion/javaHome/launchArgsFile/authlibInjectorUrl 均被 NON_NULL 省略
        var meta = JsonUtil.Parse<InstanceMeta>(Fixture("instance-minimal.json"));

        Assert.Equal("old", meta.Name);
        Assert.Equal(ServerType.PAPER, meta.ServerType);
        Assert.Equal("1.21.4", meta.GameVersion);
        Assert.Equal(2048, meta.MemoryMb);
        Assert.Equal(25565, meta.Port);          // 默认值
        Assert.Equal("server.jar", meta.LaunchJar);
        Assert.Null(meta.LoaderVersion);
        Assert.Empty(meta.Mods);
    }

    [Fact(DisplayName = "C# 写出的 instance.json 不含 null 字段（对齐 Jackson NON_NULL）")]
    public void 写出省略Null()
    {
        var meta = new InstanceMeta
        {
            Name = "beta",
            ServerType = ServerType.VANILLA,
            GameVersion = "26.3",
        };
        var json = JsonUtil.Write(meta);

        Assert.DoesNotContain("loaderVersion", json);
        Assert.DoesNotContain("javaHome", json);
        Assert.DoesNotContain("launchArgsFile", json);
        Assert.DoesNotContain("authlibInjectorUrl", json);
        Assert.DoesNotContain("lastPinggyAddress", json);
        // 但空数组要写出（Jackson 同样保留空集合）
        Assert.Contains("\"mods\": []", json);
        Assert.Contains("\"serverType\": \"VANILLA\"", json);
    }

    [Fact(DisplayName = "C# 写出形态与真实 Jackson 产物逐字段等价")]
    public void 写出形态等价()
    {
        // 读 Java 真值 → C# 重写 → 两者解析后的对象必须完全相等。
        // 这比字符串比对更可靠：Jackson 是 "k" : v、STJ 是 "k": v，缩进形态本就不同，
        // 但字段集合、值、日期格式必须一致（Java 侧才读得回）。
        var fromJava = JsonUtil.Parse<InstanceMeta>(Fixture("instance-full.json"));
        var rewritten = JsonUtil.Write(fromJava);
        var fromCSharp = JsonUtil.Parse<InstanceMeta>(rewritten);

        Assert.Equal(fromJava.Name, fromCSharp.Name);
        Assert.Equal(fromJava.ServerType, fromCSharp.ServerType);
        Assert.Equal(fromJava.GameVersion, fromCSharp.GameVersion);
        Assert.Equal(fromJava.LoaderVersion, fromCSharp.LoaderVersion);
        Assert.Equal(fromJava.MemoryMb, fromCSharp.MemoryMb);
        Assert.Equal(fromJava.JvmArgs, fromCSharp.JvmArgs);
        Assert.Equal(fromJava.Port, fromCSharp.Port);
        Assert.Equal(fromJava.JavaHome, fromCSharp.JavaHome);
        Assert.Equal(fromJava.CreatedAt, fromCSharp.CreatedAt);
        Assert.Equal(fromJava.LastPinggyAddress, fromCSharp.LastPinggyAddress);
        Assert.Equal(fromJava.LastCloudflareUrl, fromCSharp.LastCloudflareUrl);
        Assert.Equal(fromJava.Mods.Count, fromCSharp.Mods.Count);
        Assert.Equal(fromJava.Mods[0].FileName, fromCSharp.Mods[0].FileName);
        Assert.Equal(fromJava.Mods[0].InstalledAt, fromCSharp.Mods[0].InstalledAt);

        // 字段名必须是 camelCase（Java 侧靠字段名匹配，PascalCase 会静默读成默认值）
        Assert.Contains("\"name\":", rewritten);
        Assert.Contains("\"serverType\":", rewritten);
        Assert.DoesNotContain("\"Name\":", rewritten);
        Assert.DoesNotContain("\"ServerType\":", rewritten);
    }

    [Fact(DisplayName = "时间戳写出为 ISO-8601 且省略尾随零（对齐 Jackson）")]
    public void 时间戳格式()
    {
        var meta = new InstanceMeta
        {
            Name = "t",
            CreatedAt = new DateTimeOffset(2026, 10, 15, 3, 31, 44, 123, TimeSpan.Zero),
        };
        var json = JsonUtil.Write(meta);
        // 关键：不能是 System.Text.Json 默认的 7 位小数 .1230000
        Assert.Contains("\"createdAt\": \"2026-10-15T03:31:44.123Z\"", json);

        var whole = new InstanceMeta
        {
            Name = "t",
            CreatedAt = new DateTimeOffset(2026, 10, 15, 3, 31, 44, TimeSpan.Zero),
        };
        Assert.Contains("\"createdAt\": \"2026-10-15T03:31:44Z\"", JsonUtil.Write(whole));
    }

    // —— settings.json ——

    [Fact(DisplayName = "C# 能读 Java 真实写出的 settings.json")]
    public void 读Java设置()
    {
        var s = JsonUtil.Parse<AppSettings>(Fixture("settings-dark.json"));
        Assert.Equal("dark", s.Theme);
        Assert.True(s.IsDark);
        Assert.Null(s.HomePath);
        Assert.False(s.DefaultWhitelistEnabled);
    }

    [Fact(DisplayName = "Java 真实产物里没有 dark 键（闸门 B 抓到的 Java 侧 bug 已修）")]
    public void Java设置无dark泄漏()
    {
        // 实测发现：Java 版 AppSettings.isDark() 只读 getter 被 Jackson 当成属性序列化成 "dark"，
        // 而类里没有 dark 字段 → 读回时 FAIL_ON_UNKNOWN_PROPERTIES 报错 → load() 的 catch 吞掉
        // → 深色主题设置重启后静默丢失。Java 侧已加 @JsonIgnore 修复（见该文件注释）。
        // 这条断言把「修复后的形态」钉住：Java 侧若回退，本测试与 RoundTrip.java 都会红。
        //
        // 注意查的是**键**而非子串：`"theme" : "dark"` 的值里也有 dark 字样，
        // 用 DoesNotContain("\"dark\"") 会误命中，故按 JSON 键形态匹配。
        var json = Fixture("settings-dark.json");
        Assert.Contains("\"theme\"", json);
        Assert.DoesNotContain("\"dark\" :", json);   // Jackson 键形态（冒号前有空格）
        Assert.DoesNotContain("\"isDark\"", json);
        Assert.Equal(new[] { "theme" }, JsonKeys(json));
    }

    [Fact(DisplayName = "C# 的 IsDark 不得被序列化（同一个坑不能踩两次）")]
    public void CSharp设置无dark泄漏()
    {
        // AppSettings.IsDark 必须带 [JsonIgnore]：否则 STJ 会把它序列化成 "IsDark"，
        // 而 C# 读回时该属性只读 → 反序列化忽略，Java 读回时则因未知字段直接失败。
        var json = JsonUtil.Write(new AppSettings { Theme = "dark" });
        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Equal(["theme", "defaultWhitelistEnabled"], JsonKeys(json));
    }

    /// <summary>取 JSON 顶层键名（按出现顺序）。用于断言「没有多余字段」这类结构性事实。</summary>
    private static string[] JsonKeys(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    [Fact(DisplayName = "C# 写出的 settings.json 是 Java 侧真实产物（可被 JVM 读回）")]
    public void 写出设置形态()
    {
        // 与 tools/wiregen/RoundTrip.java 的 ② 步对应：JVM 已实测读回 OK。
        // 这里钉住 C# 侧的输出形态，防止将来加字段时忘记 JsonIgnore 或改坏命名策略。
        var json = Fixture("settings-from-csharp.json");
        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Contains("\"defaultWhitelistEnabled\": false", json);
        // null 字段应被省略（WhenWritingNull），Java 侧不认这两个字段，写出反而无害但没必要
        Assert.DoesNotContain("homePath", json);
        Assert.DoesNotContain("lastInstallDir", json);
    }

    [Fact(DisplayName = "settings.json 损坏时回落默认而非抛（对齐 Java 版语义）")]
    public void 设置损坏容错()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-set-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = CeobeHome.Override;
        try
        {
            Directory.CreateDirectory(dir);
            CeobeHome.Override = dir;
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");

            var s = AppSettings.Load();
            Assert.Equal("light", s.Theme);   // 回落默认
            Assert.False(s.IsDark);
        }
        finally
        {
            CeobeHome.Override = previous;
            ZipUtil.DeleteRecursively(dir);
        }
    }

    [Fact(DisplayName = "settings 写盘往返（Load/Save 成对）")]
    public void 设置写盘往返()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-set-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = CeobeHome.Override;
        try
        {
            Directory.CreateDirectory(dir);
            CeobeHome.Override = dir;

            var s = new AppSettings { Theme = "dark", DefaultWhitelistEnabled = true };
            Assert.True(s.Save());
            var back = AppSettings.Load();
            Assert.Equal("dark", back.Theme);
            Assert.True(back.IsDark);
            Assert.True(back.DefaultWhitelistEnabled);
        }
        finally
        {
            CeobeHome.Override = previous;
            ZipUtil.DeleteRecursively(dir);
        }
    }

    // —— 数据目录（整改项 3）——

    [Fact(DisplayName = "实例目录解析受 Override 影响（整改项 3 的地基）")]
    public void 数据目录可改()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ceobe-home-" + Guid.NewGuid().ToString("N")[..8]);
        var previous = CeobeHome.Override;
        try
        {
            CeobeHome.Override = tmp;
            Assert.Equal(tmp, CeobeHome.Root());
            Assert.Equal(Path.Combine(tmp, "instances"), CeobeHome.InstancesDir());
            Assert.Equal(Path.Combine(tmp, "instances", "alpha"), CeobeHome.InstanceDir("alpha"));

            // 空串不应把 home 变成 ""，应回落
            CeobeHome.Override = "  ";
            Assert.NotEqual("", CeobeHome.Root());
        }
        finally
        {
            CeobeHome.Override = previous;
            ZipUtil.DeleteRecursively(tmp);
        }
    }

    [Fact(DisplayName = "OneDrive 路径识别（整改项 3 的红字警告）")]
    public void OneDrive识别()
    {
        Assert.True(AppSettings.IsPathUnderOneDrive(@"C:\Users\xrl\OneDrive\ceobe"));
        Assert.True(AppSettings.IsPathUnderOneDrive(@"C:\Users\xrl\OneDrive\桌面\server"));
        Assert.False(AppSettings.IsPathUnderOneDrive(@"D:\games\ceobe"));
        Assert.False(AppSettings.IsPathUnderOneDrive(@"C:\Users\xrl\.ceobe"));
        Assert.False(AppSettings.IsPathUnderOneDrive(""));
    }
}
