using System.Text.Json;
using Ceobe.Core.Jdk;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>一个需要就位的库文件。</summary>
/// <param name="Name">maven 坐标（group:artifact:version），用于报错时指认。</param>
/// <param name="RelativePath">相对 libraries 根的路径（含 classifier 后缀）。</param>
/// <param name="Url">下载地址；null = 已被本地缓存覆盖（不会发生，保留以便排查）。</param>
/// <param name="Sha1">期望 SHA-1，下载后校验。</param>
/// <param name="IsNative">natives 包（需要解压到实例 natives 目录，而不是进 classpath）。</param>
public sealed record LibrarySpec(
    string Name,
    string RelativePath,
    string? Url,
    string? Sha1,
    bool IsNative);

/// <summary>
/// 装配完成、可直接用于拼命令行的一份版本定义。
/// 它由「原版版本 JSON」与「加载器 profile JSON」合并而来（Fabric 的 inheritsFrom 语义）。
/// </summary>
public sealed record ResolvedVersion(
    string VersionId,
    string GameVersion,
    string MainClass,
    string Type,
    string? AssetsIndex,
    int JavaMajor,
    IReadOnlyList<string> JvmArgs,
    IReadOnlyList<string> GameArgs,
    IReadOnlyList<LibrarySpec> Libraries,
    string ClientJarPath);

/// <summary>
/// 版本 JSON 的读取与合并（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>）。
///
/// 合并顺序很重要：原版在前、加载器 profile 在后，且 profile 的 <c>mainClass</c>
/// 必须覆盖原版——顺序反了会启动成原版（用户会以为「Mod 一个都没加载」，
/// 而日志里其实一切正常，这是最难查的一类问题）。
/// </summary>
public static class ClientVersion
{
    /// <summary>从磁盘装配某实例的版本。缺文件时给出可执行的错误，而不是 FileNotFound。</summary>
    public static ResolvedVersion Resolve(ClientMeta meta)
    {
        var vanillaJson = Path.Combine(ClientStore.VersionsDir(), meta.GameVersion, meta.GameVersion + ".json");
        if (!File.Exists(vanillaJson))
        {
            throw CeobeException.Of(
                "缺少 {0} 的版本文件，实例可能未安装完整。\n下一步：在实例的「启动」页点「修复游戏文件」，或重建实例。",
                meta.GameVersion);
        }

        var state = new MergeState();
        using (var baseDoc = ParseFile(vanillaJson))
        {
            state.Apply(baseDoc.RootElement, isBase: true);
        }

        if (meta.Loader != ClientLoader.VANILLA)
        {
            var loaderId = LoaderVersionId(meta);
            var profileJson = Path.Combine(ClientStore.VersionsDir(), loaderId, loaderId + ".json");
            if (!File.Exists(profileJson))
            {
                throw CeobeException.Of(
                    "缺少加载器 {0} 的 profile（{1}），实例未安装完整。\n下一步：点「修复游戏文件」重新装配。",
                    meta.Loader, loaderId);
            }
            using var profileDoc = ParseFile(profileJson);
            state.Apply(profileDoc.RootElement, isBase: false);
        }

        if (string.IsNullOrWhiteSpace(state.MainClass))
            throw new CeobeException("版本 JSON 没有 mainClass，无法启动");

        var jar = ClientJar(meta.GameVersion);
        if (!File.Exists(jar))
        {
            throw CeobeException.Of(
                "缺少客户端 {0}.jar。\n下一步：点「修复游戏文件」重新下载。", meta.GameVersion);
        }

        return new ResolvedVersion(
            state.VersionId ?? meta.GameVersion,
            meta.GameVersion,
            state.MainClass!,
            state.Type ?? "release",
            state.AssetsIndex,
            state.JavaMajor ?? 8,
            state.JvmArgs,
            state.GameArgs,
            state.Libraries,
            jar);
    }

    /// <summary>单独解析库列表（安装时用：此时还没有合并，只要知道该下哪些文件）。</summary>
    public static IReadOnlyList<LibrarySpec> ParseLibraries(JsonElement root)
    {
        var state = new MergeState();
        state.Apply(root, isBase: true);
        return state.Libraries;
    }

    /// <summary>加载器 profile 的版本 id，与安装时写盘用的 id 必须一致。</summary>
    public static string LoaderVersionId(ClientMeta meta) => meta.Loader switch
    {
        ClientLoader.FABRIC => $"{meta.GameVersion}-fabric{meta.LoaderVersion}",
        ClientLoader.QUILT => $"{meta.GameVersion}-quilt{meta.LoaderVersion}",
        ClientLoader.NEOFORGE => $"neoforge-{meta.LoaderVersion}",
        ClientLoader.FORGE => $"{meta.GameVersion}-forge-{meta.LoaderVersion}",
        _ => meta.GameVersion,
    };

    /// <summary>共享缓存里的客户端 jar（所有实例共用一份，按版本 id 区分）。</summary>
    public static string ClientJar(string gameVersion) =>
        Path.Combine(ClientStore.VersionsDir(), gameVersion, gameVersion + ".jar");

    public static string VersionJson(string versionId) =>
        Path.Combine(ClientStore.VersionsDir(), versionId, versionId + ".json");

    /// <summary>
    /// 读已落盘的版本 JSON 里的 Java 主版本；文件缺失时退回 <see cref="JavaRequirement"/> 的兜底值。
    /// 用于「新建实例」预览「将复用哪个 Java」——此时还没安装，只能靠缓存或兜底。
    /// </summary>
    public static int ReadJavaMajorFromCache(string gameVersion)
    {
        var path = VersionJson(gameVersion);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonUtil.Parse(File.ReadAllText(path));
                var major = doc.RootElement.Prop("javaVersion").Prop("majorVersion").AsInt(0);
                if (major > 0) return major;
            }
            catch (Exception)
            {
            }
        }
        return JavaRequirement.CachedMajorOrFallback(gameVersion);
    }

    private static JsonDocument ParseFile(string path)
    {
        try
        {
            return JsonUtil.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"读取版本文件失败: {path}", e);
        }
    }

    /// <summary>合并过程中的可变状态（原版与 profile 各写一次）。</summary>
    private sealed class MergeState
    {
        public string? VersionId;
        public string? MainClass;
        public string? Type;
        public string? AssetsIndex;
        public int? JavaMajor;
        public readonly List<string> JvmArgs = [];
        public readonly List<string> GameArgs = [];
        public readonly List<LibrarySpec> Libraries = [];

        public void Apply(JsonElement root, bool isBase)
        {
            VersionId ??= root.Prop("id").AsText();
            // 加载器的 mainClass 必须赢；原版只在还没有值时填
            MainClass = isBase ? MainClass ?? root.Prop("mainClass").AsText() : root.Prop("mainClass").AsText() ?? MainClass;
            Type ??= root.Prop("type").AsText();
            AssetsIndex ??= root.Prop("assetIndex").Prop("id").AsText() ?? root.Prop("assets").AsText();

            if (JavaMajor is null)
            {
                var major = root.Prop("javaVersion").Prop("majorVersion").AsInt(0);
                if (major > 0) JavaMajor = major;
            }

            // 1.13 之前没有 arguments 对象，只有一行 minecraftArguments 字符串
            var legacy = root.Prop("minecraftArguments").AsText();
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                GameArgs.AddRange(legacy!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            var args = root.Prop("arguments");
            AppendArguments(args.Prop("jvm"), JvmArgs);
            AppendArguments(args.Prop("game"), GameArgs);

            foreach (var lib in root.Prop("libraries").Items())
            {
                var spec = ParseLibrary(lib);
                if (spec is not null) Libraries.Add(spec);
            }
        }

        /// <summary>arguments 是「字符串 / 带 rules 的对象」混合数组，按规则过滤后展开。</summary>
        private static void AppendArguments(JsonElement array, List<string> target)
        {
            foreach (var item in array.Items())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    target.Add(item.GetString() ?? "");
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!AllowedByRules(item.Prop("rules"))) continue;
                foreach (var value in item.Prop("value").Strings()) target.Add(value);
            }
        }

        private static LibrarySpec? ParseLibrary(JsonElement lib)
        {
            if (!AllowedByRules(lib.Prop("rules"))) return null;

            var name = lib.Prop("name").AsText() ?? "";
            if (name.Length == 0) return null;

            // natives：只有 windows 分类的条目需要解压
            var natives = lib.Prop("natives");
            if (!natives.IsMissing())
            {
                var classifier = natives.Prop("windows").AsText();
                if (string.IsNullOrEmpty(classifier)) return null;   // 该库没有 Windows natives
                classifier = classifier.Replace("${arch}", "64");
                var art = lib.Prop("downloads").Prop("classifiers").Prop(classifier!);
                var rel = art.Prop("path").AsText() ?? MavenPath(name, classifier);
                return new LibrarySpec(name, rel,
                    art.Prop("url").AsText(),
                    art.Prop("sha1").AsText(),
                    IsNative: true);
            }

            var artifact = lib.Prop("downloads").Prop("artifact");
            var path = artifact.Prop("path").AsText();
            var url = artifact.Prop("url").AsText();

            if (string.IsNullOrEmpty(path))
            {
                // Fabric/Quilt profile 只给 maven 坐标 + 仓库基址
                var baseUrl = lib.Prop("url").AsText();
                if (string.IsNullOrEmpty(baseUrl)) return null;   // 没有来源，跳过（如已内置的库）
                path = MavenPath(name, null);
                url = baseUrl!.TrimEnd('/') + "/" + path;
            }

            return new LibrarySpec(name, path!, url, artifact.Prop("sha1").AsText(),
                IsNative: IsNativeArtifact(name, path!));
        }

        /// <summary>
        /// 判断一个库是不是 natives 包。1.19+ 不再用 <c>natives</c> 字段，而是把
        /// <c>org.lwjgl:lwjgl:3.3.1:natives-windows</c> 这类坐标直接列进 libraries——
        /// 它们要解压进 natives 目录，**不能**留在 classpath 上（否则游戏报 UnsatisfiedLinkError）。
        /// </summary>
        private static bool IsNativeArtifact(string name, string path) =>
            name.Contains(":natives-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("-natives-", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Minecraft 的 rules 语义：默认不允许；按顺序命中则被最后命中的规则覆盖。
        /// 未写 rules = 适用所有平台。只实现 Windows 与 x86_64 两个维度——
        /// 本项目是 Windows 专属（ADR-019），把 mac/linux 分支写进来只会是死代码。
        /// </summary>
        internal static bool AllowedByRules(JsonElement rules)
        {
            if (rules.IsMissing()) return true;
            if (rules.ValueKind != JsonValueKind.Array) return true;

            var allowed = false;
            foreach (var rule in rules.Items())
            {
                if (rule.ValueKind != JsonValueKind.Object) continue;
                if (!RuleMatches(rule)) continue;
                allowed = string.Equals(rule.Prop("action").AsText(), "allow", StringComparison.OrdinalIgnoreCase);
            }
            return allowed;
        }

        private static bool RuleMatches(JsonElement rule)
        {
            var os = rule.Prop("os");
            if (!os.IsMissing())
            {
                var osName = os.Prop("name").AsText();
                if (!string.IsNullOrEmpty(osName)
                    && !string.Equals(osName, "windows", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var arch = os.Prop("arch").AsText();
                if (!string.IsNullOrEmpty(arch)
                    && !string.Equals(arch, "x86_64", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(arch, "x64", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(arch, "amd64", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var version = os.Prop("version").AsText();
                if (!string.IsNullOrEmpty(version)) return false;   // 正则匹配 Windows 版本，极少用，保守跳过
            }

            // features：demo 用户 / 自定义分辨率 / 快捷进入世界——ceobe 都不开启这些开关
            var features = rule.Prop("features");
            if (!features.IsMissing())
            {
                foreach (var f in features.Properties())
                {
                    if (f.Value.AsBool()) return false;
                }
            }
            return true;
        }
    }

    /// <summary>maven 坐标 → 仓库相对路径（group 的点换成斜杠）。</summary>
    internal static string MavenPath(string name, string? classifier)
    {
        var at = name.IndexOf('@');
        var ext = at >= 0 ? name[(at + 1)..] : "jar";
        if (at >= 0) name = name[..at];

        var parts = name.Split(':');
        if (parts.Length < 3) return name + "." + ext;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var cls = parts.Length > 3 ? parts[3] : classifier;
        var file = cls is null
            ? $"{artifact}-{version}.{ext}"
            : $"{artifact}-{version}-{cls}.{ext}";
        return $"{group}/{artifact}/{version}/{file}";
    }
}
