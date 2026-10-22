using System.Text;
using Ceobe.Core.Jdk;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>一次启动的完整计划（命令、工作目录、解析出的版本）。</summary>
public sealed record LaunchPlan(
    string JavaExe,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    ResolvedVersion Version,
    IReadOnlyList<string> Classpath,
    string NativesDirectory)
{
    /// <summary>供「日志」页与诊断展示的命令行（敏感令牌打码）。</summary>
    public string DisplayCommand
    {
        get
        {
            var sb = new StringBuilder(JavaExe.Contains(' ') ? "\"" + JavaExe + "\"" : JavaExe);
            foreach (var a in Arguments)
            {
                sb.Append(' ');
                sb.Append(a.Contains(' ') ? "\"" + a + "\"" : a);
            }
            return sb.ToString();
        }
    }
}

/// <summary>
/// 拼装启动命令行。这是启动器的心脏，也是最容易写成「只在作者机器上能跑」的地方。
///
/// 三条不能省的处理：
/// <list type="number">
///   <item>**占位符**：<c>${auth_player_name}</c> 这类占位符来自版本 JSON，漏一个游戏就起不来，
///         且报错往往只有一句 <c>--username 缺失</c>；</item>
///   <item>**Java 版本**：要求取自版本 JSON 的 <c>javaVersion.majorVersion</c>（权威），
///         而不是我们猜的映射表；解析失败必须给出可执行的下一步（ADR-018）；</item>
///   <item>**classpath 顺序**：natives 包绝不能进 classpath，client.jar 放最后（原版 jar 里的
///         类应被 Mod 加载器覆盖，而不是反过来）。</item>
/// </list>
/// </summary>
public static class ClientLauncher
{
    public const string LauncherName = "ceobe";

    public static LaunchPlan Build(ClientMeta meta, ClientAccount account,
        string? serverAddress = null, ClientSettings? settings = null)
    {
        settings ??= ClientSettings.Load();
        var version = ClientVersion.Resolve(meta);

        // —— Java ——
        var desiredHome = !string.IsNullOrWhiteSpace(meta.JavaHome) ? meta.JavaHome : settings.JavaHome;
        var resolution = JavaResolver.Resolve(meta.GameVersion, version.JavaMajor, desiredHome,
            requirementKnown: version.JavaMajor > 0);
        if (!resolution.Ok || resolution.JavaHome is null)
        {
            throw new CeobeException(resolution.Message ?? "找不到可用的 Java 运行时");
        }
        var javaExe = Path.Combine(resolution.JavaHome, "bin", JavaEnv.JavaExe);
        if (!File.Exists(javaExe))
        {
            throw CeobeException.Of("Java 启动器不存在：{0}\n下一步：在「设置」里重新选择 JDK 目录", javaExe);
        }

        var natives = ClientStore.NativesDir(meta.Name);
        Directory.CreateDirectory(natives);

        var classpath = version.Libraries
            .Where(l => !l.IsNative)
            .Select(l => Path.Combine(ClientStore.LibrariesDir(),
                l.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();
        classpath.Add(version.ClientJarPath);

        var vars = BuildVariables(meta, account, version, natives, classpath, serverAddress);

        var args = new List<string>();

        // —— JVM ——
        // 现代版本 JSON 自带完整 jvm 参数（含 -cp、-Djava.library.path）；
        // 1.13 之前没有，需要我们自己补。判断依据是 GameArgs 里有没有 jvm 参数，
        // 用「JvmArgs 是否为空」更直接。
        var hasModernJvm = version.JvmArgs.Count > 0;
        if (!hasModernJvm)
        {
            args.Add("-Djava.library.path=" + natives);
            args.Add("-cp");
            args.Add(string.Join(Path.PathSeparator, classpath));
        }
        foreach (var a in version.JvmArgs)
        {
            var expanded = Substitute(a, vars);
            if (expanded.Length > 0) args.Add(expanded);
        }

        // 堆与 GC：实例内存是硬约束，全局参数在前、实例参数在后（后者可覆盖）
        args.Add($"-Xmx{meta.MemoryMb}M");
        args.Add($"-Xms{Math.Min(meta.MemoryMb, 1024)}M");
        args.Add("-Dminecraft.launcher.brand=" + LauncherName);
        args.Add("-Dminecraft.launcher.version=1.0");
        // 老版本（尤其 1.12 的 Forge）在 Java 17+ 上常因反射封装直接崩，这里不强行加 --add-opens：
        // 加了会污染现代版本，且不同 Forge 需要的包并不一致。改为在诊断里提示换 Java 8/17。

        foreach (var extra in SplitArgs(settings.JvmArgs)) args.Add(extra);
        foreach (var extra in SplitArgs(meta.JvmArgs)) args.Add(extra);

        args.Add(version.MainClass);

        // —— 游戏参数 ——
        foreach (var a in version.GameArgs)
        {
            var expanded = Substitute(a, vars);
            if (expanded.Length > 0) args.Add(expanded);
        }
        foreach (var extra in SplitArgs(meta.GameArgs)) args.Add(extra);

        // 直连：从「服务器」分区点「加入」时，直接带 --server/--port，跳过主菜单
        if (!string.IsNullOrWhiteSpace(serverAddress))
        {
            var (host, port) = SplitAddress(serverAddress);
            args.Add("--server");
            args.Add(host);
            args.Add("--port");
            args.Add(port.ToString());
        }

        return new LaunchPlan(javaExe, args, ClientStore.Dir(meta.Name), version, classpath, natives);
    }

    private static Dictionary<string, string> BuildVariables(ClientMeta meta, ClientAccount account,
        ResolvedVersion version, string natives, IReadOnlyList<string> classpath, string? serverAddress)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = version.VersionId,
            ["game_directory"] = ClientStore.Dir(meta.Name),
            ["assets_root"] = ClientStore.AssetsDir(),
            ["assets_index_name"] = version.AssetsIndex ?? "legacy",
            ["auth_uuid"] = account.EffectiveUuid,
            ["auth_access_token"] = string.IsNullOrWhiteSpace(account.AccessToken) ? "0" : account.AccessToken!,
            ["clientid"] = "ceobe",
            ["auth_xuid"] = "",
            ["user_type"] = account.Kind == AccountKind.MICROSOFT ? "msa" : "legacy",
            ["version_type"] = version.Type,
            ["natives_directory"] = natives,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = "1.0",
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            // 1.6 及更早的资源目录占位符
            ["game_assets"] = Path.Combine(ClientStore.AssetsDir(), "virtual", "legacy"),
            ["library_directory"] = ClientStore.LibrariesDir(),
        };
    }

    /// <summary>
    /// 占位符替换。未识别的 <c>${x}</c> 原样保留——静默替换成空串会让参数悄悄少一段，
    /// 而保留至少能在命令行里一眼看出缺了什么。
    /// </summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, string> vars)
    {
        if (!template.Contains("${", StringComparison.Ordinal)) return template;
        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, start - i);
            var end = template.IndexOf('}', start);
            if (end < 0)
            {
                sb.Append(template, start, template.Length - start);
                break;
            }
            var key = template[(start + 2)..end];
            sb.Append(vars.TryGetValue(key, out var value) ? value : template[start..(end + 1)]);
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>按 shell 习惯切分参数串（支持双引号），用于实例的「附加 JVM/游戏参数」输入框。</summary>
    internal static IReadOnlyList<string> SplitArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in raw)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    internal static (string Host, int Port) SplitAddress(string address)
    {
        var a = address.Trim();
        var colon = a.LastIndexOf(':');
        if (colon > 0 && int.TryParse(a[(colon + 1)..], out var port) && port is > 0 and <= 65535)
        {
            return (a[..colon], port);
        }
        return (a, 25565);
    }
}
