using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Store;

/// <summary>
/// instance.json / settings.json 的 **wire 兼容性守卫**（docs/MIGRATION.md 闸门 B 的静态版本）。
///
/// 为什么这份测试重要：迁移期 Java 版与 C# 版要能读写同一份实例目录。
/// Jackson 输出的 key 是小驼峰、日期是 ISO-8601 且省略尾随零、null 字段不写出、枚举是大写名。
/// 任何一个属性改名、加了 [JsonPropertyName]、或换了日期格式，都会让另一侧读回默认值——
/// 而且**不会报错，只会静默丢数据**（比如 mod 清单变空、端口回到 25565）。这类 bug 只能靠守卫测试挡。
/// 下面这段 JSON 是 Java 版 Jackson 的真实输出形态。
/// </summary>
public class WireCompatibilityTest
{
    /// <summary>Java 版 <c>Json.write(meta)</c> 的等价产物（含 mod、含 null 省略）。</summary>
    private const string FromJava = """
    {
      "name" : "alpha",
      "serverType" : "FABRIC",
      "gameVersion" : "26.3",
      "loaderVersion" : "0.16.9",
      "memoryMb" : 4096,
      "jvmArgs" : "-XX:+UseG1GC",
      "port" : 25570,
      "javaHome" : "C:\\jdk-25",
      "launchJar" : "server.jar",
      "createdAt" : "2026-10-15T03:31:44.123Z",
      "mods" : [ {
        "projectId" : "PpRxcduo",
        "slug" : "fabric-api",
        "title" : "Fabric API",
        "versionNumber" : "0.98.0+1.21.4",
        "fileName" : "fabric-api-0.98.0.jar",
        "sha1" : "0123456789abcdef0123456789abcdef01234567",
        "envServer" : "required",
        "envClient" : "required",
        "installedAt" : "2026-10-15T03:40:00Z"
      } ],
      "lastPinggyAddress" : "tcp://x.run.pinggy-free.link:35663",
      "lastCloudflareUrl" : "https://abc-def-ghi.trycloudflare.com"
    }
    """;

    [Fact(DisplayName = "能读 Java 版写出的 instance.json（逐字段）")]
    public void 读Java产物()
    {
        var meta = JsonUtil.Parse<InstanceMeta>(FromJava);

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

        // 时间戳：ISO 无尾随零必须被正确吸收
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

        Assert.DoesNotContain("\"loaderVersion\"", json);
        Assert.DoesNotContain("\"javaHome\"", json);
        Assert.DoesNotContain("\"launchArgsFile\"", json);
        Assert.DoesNotContain("\"authlibInjectorUrl\"", json);
        Assert.DoesNotContain("\"lastPinggyAddress\"", json);
        // 但空数组要写出（Jackson 同样保留空集合）
        Assert.Contains("\"mods\": []", json);
        Assert.Contains("\"serverType\": \"VANILLA\"", json);
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

        var whole = new InstanceMeta { Name = "t", CreatedAt = new DateTimeOffset(2026, 10, 15, 3, 31, 44, TimeSpan.Zero) };
        Assert.Contains("\"createdAt\": \"2026-10-15T03:31:44Z\"", JsonUtil.Write(whole));
    }

    [Fact(DisplayName = "写出-读回往返无损")]
    public void 往返无损()
    {
        var original = JsonUtil.Parse<InstanceMeta>(FromJava);
        var round = JsonUtil.Parse<InstanceMeta>(JsonUtil.Write(original));

        Assert.Equal(original.Name, round.Name);
        Assert.Equal(original.ServerType, round.ServerType);
        Assert.Equal(original.GameVersion, round.GameVersion);
        Assert.Equal(original.LoaderVersion, round.LoaderVersion);
        Assert.Equal(original.MemoryMb, round.MemoryMb);
        Assert.Equal(original.Port, round.Port);
        Assert.Equal(original.JavaHome, round.JavaHome);
        Assert.Equal(original.CreatedAt, round.CreatedAt);
        Assert.Equal(original.Mods.Count, round.Mods.Count);
        Assert.Equal(original.Mods[0].FileName, round.Mods[0].FileName);
        Assert.Equal(original.Mods[0].InstalledAt, round.Mods[0].InstalledAt);
    }

    [Fact(DisplayName = "缺字段宽容读回默认值（不因 Java 侧字段缺失而崩）")]
    public void 缺字段宽容()
    {
        // 老版本 instance.json 没有 authlibInjectorUrl / launchArgsFile（ADR-010/015 之后才加）
        var minimal = """{ "name": "old", "serverType": "PAPER", "gameVersion": "1.21.4", "memoryMb": 2048 }""";
        var meta = JsonUtil.Parse<InstanceMeta>(minimal);
        Assert.Equal("old", meta.Name);
        Assert.Equal(ServerType.PAPER, meta.ServerType);
        Assert.Equal(25565, meta.Port);          // 默认值
        Assert.Equal("server.jar", meta.LaunchJar);
        Assert.Empty(meta.Mods);
    }

    [Fact(DisplayName = "settings.json 可读 Java 版产物并保留 theme")]
    public void 读设置()
    {
        var s = JsonUtil.Parse<AppSettings>("""{ "theme": "dark" }""");
        Assert.Equal("dark", s.Theme);
        Assert.True(s.IsDark);
        Assert.Null(s.HomePath);
    }

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
