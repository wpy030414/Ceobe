using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using Ceobe.Core.Model;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Mods;

/// <summary>
/// 一键导入识别器（整改项 4，PCL 式）：给一个本地文件，产出「识别 → 预览」计划，
/// 由调用方（GUI 确认页）拿用户确认后再调 <see cref="ModImporter.InstallAsync"/> 落盘。
///
/// 设计红线：
/// - 绝不静默猜测：拿不准就标 <see cref="ImportKind.ServerJarCandidate"/> / Unknown 并给依据；
/// - 绝不自动写盘：Inspect 只读，InstallAsync 需要显式实例目录与（默认拒绝 Mismatch 的）确认开关；
/// - 只嗅探 zip 条目标记，不解析 mod 代码语义（例外：mrpack 索引与匹配字段属包自身契约，允许读取）。
/// </summary>
public static class ModImporter
{
    // —— 标记表（嗅探规则，逐字对齐任务书）——

    /// <summary>zip 根条目 → 判定（先命中先得，见 <see cref="Inspect(string)"/> 的决策表注释）。</summary>
    private const string MrpackMarker = "modrinth.index.json";

    private static readonly string[] MultiMcMarkers = ["version.json", "manifest.yml", "manifest-minecraft.json"];

    /// <summary>mrpack dependencies 键 → 归一化 loader（与 MrpackInstaller 同一张表，此处内部复制以免暴露地基 API）。</summary>
    private static readonly (string Key, string Name)[] MrpackLoaderDeps =
    [
        ("fabric-loader", "fabric"),
        ("neoforge", "neoforge"),
        ("forge", "forge"),
        ("quilt-loader", "quilt"),
    ];

    private const string FabricMarker = "fabric.mod.json";
    private const string NeoForgeMarker = "META-INF/neoforge.mods.toml";
    private const string ForgeMarker = "META-INF/mods.toml";
    private const string LegacyForgeMarker = "mcmod.info";

    private const string ModsDirPrefix = "mods/";

    private static readonly string[] DepDirPrefixes = ["dependencies/", "libraries/"];

    /// <summary>识别出的种类。</summary>
    public enum ImportKind
    {
        /// <summary>Modrinth 整合包（.mrpack 或含 modrinth.index.json 的归档）。</summary>
        Mrpack,
        /// <summary>Fabric 模组 jar（fabric.mod.json）。</summary>
        FabricMod,
        /// <summary>Forge 系模组 jar（META-INF/mods.toml）。</summary>
        ForgeMod,
        /// <summary>NeoForge 模组 jar（META-INF/neoforge.mods.toml）。</summary>
        NeoForgeMod,
        /// <summary>旧版 Forge 模组 jar（mcmod.info，MC ≤1.12 时代）。</summary>
        LegacyForgeMod,
        /// <summary>mod 合集压缩包（含 mods/ 目录）。</summary>
        ModsBundle,
        /// <summary>MultiMC / PCL 实例格式（version.json / manifest.yml）——暂不完整支持，仅列出可识别内容。</summary>
        MultiMcPack,
        /// <summary>zip 标记全部未命中的 jar：可能是服务端 jar（标出，不断言）。</summary>
        ServerJarCandidate,
        /// <summary>未知：既不是 zip 也没有任何标记，需用户指定用途。</summary>
        Unknown,
    }

    /// <summary>识别置信度。Ambiguous = 需要用户裁决，不允许自动落盘。</summary>
    public enum ImportConfidence
    {
        /// <summary>包内标记直接命中，判定确定。</summary>
        Confirmed,
        /// <summary>倾向性判定（如「可能是服务端 jar」）。</summary>
        Likely,
        /// <summary>未知，需用户指定用途。</summary>
        Ambiguous,
    }

    /// <summary>与目标实例的匹配判断。保守原则：拿不准一律 <see cref="Unknown"/>，绝不返回乐观的 Matches。</summary>
    public enum MatchVerdict
    {
        /// <summary>确定匹配（仅当版本/加载器都能确证相等）。</summary>
        Matches,
        /// <summary>确定不匹配。</summary>
        Mismatch,
        /// <summary>不确定 / 无目标实例信息。</summary>
        Unknown,
    }

    /// <summary>待写入清单中的一项：sourceEntry 为归档内条目名（单文件直接复制时为 null，用 SourcePath 整体）。</summary>
    public sealed record ImportFile(
        string RelativeTarget,
        string? SourceEntry,
        long Bytes,
        bool AlreadyExists);

    /// <summary>识别结果（预览）。不写任何文件。</summary>
    public sealed record ImportPlan
    {
        public required string SourcePath { get; init; }
        public required ImportKind Kind { get; init; }
        public required ImportConfidence Confidence { get; init; }

        /// <summary>判定依据：命中了哪些包内标记 / 扩展名推断 / 未命中说明。</summary>
        public required IReadOnlyList<string> Evidence { get; init; }

        /// <summary>建议落位子目录（相对实例根，如 "mods"；mrpack 为根目录自行分发）。</summary>
        public required string SuggestedSubdir { get; init; }

        /// <summary>待写入文件清单（预览用，可能为「索引声明清单」而非实际字节级复制清单）。</summary>
        public required IReadOnlyList<ImportFile> Files { get; init; }

        public long TotalBytes { get; init; }

        /// <summary>识别出的加载器（fabric/neoforge/forge/quilt；未知 null）。</summary>
        public string? DetectedLoader { get; init; }

        /// <summary>识别出的游戏版本约束原文（"*"、"~26.3"、"[1.20,)" 等，不做语义展开）。</summary>
        public string? DetectedGameVersion { get; init; }

        /// <summary>识别出的运行环境（fabric 的 environment：client/server/*）；未声明为 null。</summary>
        public string? DetectedEnvironment { get; init; }

        /// <summary>与目标实例的匹配（未提供 meta 时为 Unknown）。</summary>
        public MatchVerdict Match { get; init; } = MatchVerdict.Unknown;

        /// <summary>匹配结论的人话解释（GUI 直接展示）。</summary>
        public string MatchReason { get; init; } = "";

        /// <summary>附加提示：可能缺失依赖 / 暂不完整支持 / 重名冲突等。</summary>
        public IReadOnlyList<string> Notes { get; init; } = [];

        /// <summary>能否执行 InstallAsync（MultiMcPack / ServerJarCandidate / Unknown → false）。</summary>
        public bool Actionable { get; init; }
    }

    /// <summary>执行安装的结果。</summary>
    public sealed record ImportResult(
        ImportKind Kind,
        int WrittenFiles,
        long WrittenBytes,
        IReadOnlyList<ModEntry> Records,
        string Message);

    /// <summary>
    /// 预览：识别本地文件，绝不写盘。决策顺序（先命中先得，与任务书一致）：
    /// ① 根含 modrinth.index.json → Mrpack；
    /// ② 模组标记：fabric.mod.json → FabricMod；META-INF/neoforge.mods.toml → NeoForgeMod；
    ///    META-INF/mods.toml → ForgeMod；mcmod.info → LegacyForgeMod；
    /// ③ 含 mods/ 目录 → ModsBundle；
    /// ④ 根含 version.json（带 formatVersion 键）或 manifest.yml / manifest-minecraft.json → MultiMcPack
    ///    （暂不完整支持，仅列出可识别内容；原版服务端 jar 的 version.json 缺 formatVersion，不在此列）；
    /// ⑤ 全未命中 → ServerJarCandidate（标出「可能是服务端 jar」，不断言）。
    /// 任一环节读不了 zip → Unknown。
    /// </summary>
    public static ImportPlan Inspect(string path) => Inspect(path, null, null);

    /// <summary>预览 + 匹配评估：提供 <paramref name="meta"/> 时附带与目标实例的保守比较；
    /// 提供 <paramref name="instanceDir"/> 时额外标注重名冲突。</summary>
    public static ImportPlan Inspect(string path, InstanceMeta? meta, string? instanceDir = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw CeobeException.Of("导入目标不存在: {0}", path);
        }

        var ext = Path.GetExtension(full).ToLowerInvariant();
        Sniff? sniff = TrySniffZip(full);

        if (sniff is null)
        {
            // 不是 zip（或损坏）：jar 扩展名也无从判定
            return Finalize(new ImportPlan
            {
                SourcePath = full,
                Kind = ImportKind.Unknown,
                Confidence = ImportConfidence.Ambiguous,
                Evidence = [$"扩展名 {ext}，无法作为 zip 读取，内容无任何可识别标记"],
                SuggestedSubdir = "mods",
                Files = [],
                Notes = ["未知类型：需用户指定用途。若这是普通文件，请直接放入目标目录"],
                Actionable = false,
            }, meta, instanceDir);
        }

        var s = sniff.Value;
        var notes = new List<string>();
        foreach (var p in DepDirPrefixes)
        {
            if (s.Names.Any(n => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                                 || n.Equals(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"包内含 {p} 目录：可能声明了额外依赖，本工具不会自动补齐");
                break;
            }
        }

        ImportPlan plan;
        if (s.HasEntry(MrpackMarker))
        {
            plan = BuildMrpackPlan(full, s, notes);
        }
        else if (s.HasEntry(FabricMarker))
        {
            var (loader, gv, env, envNote) = ReadFabricMeta(full, s);
            if (envNote is not null) notes.Add(envNote);
            plan = SingleJarPlan(full, s, ImportKind.FabricMod, FabricMarker, loader, gv, notes)
                with { DetectedEnvironment = env };
        }
        else if (s.HasEntry(NeoForgeMarker))
        {
            plan = SingleJarPlan(full, s, ImportKind.NeoForgeMod, NeoForgeMarker, "neoforge",
                ReadTomlMcVersion(full, NeoForgeMarker), notes);
        }
        else if (s.HasEntry(ForgeMarker))
        {
            plan = SingleJarPlan(full, s, ImportKind.ForgeMod, ForgeMarker, "forge",
                ReadTomlMcVersion(full, ForgeMarker), notes);
        }
        else if (s.HasEntry(LegacyForgeMarker))
        {
            plan = SingleJarPlan(full, s, ImportKind.LegacyForgeMod, LegacyForgeMarker, "forge",
                ReadLegacyForgeMcVersion(full), notes);
        }
        else if (s.HasModsDir)
        {
            plan = BuildBundlePlan(full, s, notes);
        }
        else if (IsMultiMc(full, s))
        {
            // 任务书顺序：mrpack → mods/ 合集 → version.json/manifest.yml（MultiMC/PCL）。
            // PCL 导出常同时带 mods/ 与 version.json，按此顺序先走上面的 mods/ 合集分支（更可行动）；
            // 纯实例包（无 mods/）才落到这里标「暂不完整支持」。原版服务端 jar 内也带 version.json，
            // 但缺 formatVersion 键——IsMultiMc 据此排除，它会落到下面的 ServerJarCandidate 分支（保守）。
            plan = BuildMultiMcPlan(full, s, notes);
        }
        else
        {
            // 决策表 ⑤：全部未命中——标出而不是断言
            plan = new ImportPlan
            {
                SourcePath = full,
                Kind = ImportKind.ServerJarCandidate,
                Confidence = ImportConfidence.Likely,
                Evidence =
                [
                    "zip 可读，但 " + FabricMarker + " / " + ForgeMarker + " / " + NeoForgeMarker +
                    " / " + LegacyForgeMarker + " / " + MrpackMarker + " 与 mods/ 均未命中",
                    s.HasEntry("version.json")
                        ? "version.json 存在但缺 formatVersion 键：更像原版服务端 jar 而非 MultiMC 实例"
                        : "manifest.yml / version.json（实例格式）未命中",
                    $"扩展名 {ext}",
                ],
                SuggestedSubdir = "",
                Files = [new ImportFile(Path.GetFileName(full), null, new FileInfo(full).Length, false)],
                TotalBytes = new FileInfo(full).Length,
                Notes =
                [
                    .. notes,
                    "可能是服务端 jar：本工具的服务端 jar 由 providers 安装，不从这里落地；" +
                    "若确认是 mod（如未打包标记的插件），请手动指定用途后放入 mods/",
                ],
                Actionable = false,
            };
        }

        return Finalize(plan, meta, instanceDir);
    }

    /// <summary>Inspect 的异步包装（纯本地读，无网络；GUI 线程调用避免卡顿）。</summary>
    public static Task<ImportPlan> InspectAsync(string path, InstanceMeta? meta, string? instanceDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => Inspect(path, meta, instanceDir), ct);

    /// <summary>
    /// 执行安装：把预览计划真正落盘。要求显式目标实例目录；
    /// <paramref name="plan"/> 必须是 <see cref="Inspect(string, InstanceMeta, string)"/>（传 meta 才会做匹配）的产物——
    /// Mismatch 默认拒绝，用户在确认页坚持时传 <paramref name="allowMismatch"/>=true 强行导入。
    /// </summary>
    public static async Task<ImportResult> InstallAsync(
        ImportPlan plan, string instanceDir, bool allowMismatch = false,
        IProgressSink? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Actionable)
        {
            throw CeobeException.Of("「{0}」当前不可自动导入：{1}", plan.Kind,
                plan.Notes.Count > 0 ? plan.Notes[0] : "需用户指定用途");
        }
        if (plan.Match == MatchVerdict.Mismatch && !allowMismatch)
        {
            throw CeobeException.Of("与目标实例不匹配：{0}（如确认强行导入请指定 allowMismatch）", plan.MatchReason);
        }

        var sink = progress ?? NullProgress.Instance;
        var fullInstance = Path.GetFullPath(instanceDir);
        CeobeHome.Ensure(fullInstance);

        if (plan.Kind == ImportKind.Mrpack)
        {
            // 复用 mrpack 同一套解析/安装（含硬契约 6 语义）
            var r = await MrpackInstaller.InstallFromLocalAsync(fullInstance, plan.SourcePath, sink, ct)
                .ConfigureAwait(false);
            var packMsg = $"整合包「{r.Name} {r.VersionId}」：下载 {r.Files} 个文件，跳过 {r.Skipped}，覆盖 {r.Overrides}";
            sink.Update(packMsg, 1, 1);
            return new ImportResult(plan.Kind, r.Files + r.Overrides, 0, [], packMsg);
        }

        if (plan.Kind == ImportKind.ModsBundle)
        {
            return await InstallBundleAsync(plan, fullInstance, sink, ct).ConfigureAwait(false);
        }

        // 单文件 mod jar 路线（Fabric/Forge/NeoForge/LegacyForge）
        var modsDir = CeobeHome.Ensure(Path.Combine(fullInstance, "mods"));
        var fileName = Path.GetFileName(plan.SourcePath);
        var target = Path.Combine(modsDir, fileName);
        var replaced = File.Exists(target);
        sink.Update("复制模组 " + fileName, 0, new FileInfo(plan.SourcePath).Length);
        try
        {
            File.Copy(plan.SourcePath, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("复制模组失败: " + fileName, e);
        }
        var entry = MakeRecord(fileName, target, plan.DetectedLoader);
        var msg = replaced
            ? $"已替换同名模组 {fileName}（mods/ 原有文件被覆盖）"
            : $"已导入 {fileName} → mods/";
        sink.Update(msg, 1, 1);
        return new ImportResult(plan.Kind, 1, new FileInfo(target).Length, [entry], msg);
    }

    // —— 内部：识别 ——

    private readonly struct Sniff
    {
        public required HashSet<string> Names { get; init; }
        public required List<(string Entry, string Relative, long Bytes)> BundleFiles { get; init; }

        /// <summary>是否存在 mods/ 目录（含空目录条目）。</summary>
        public bool HasModsDir { get; init; }

        public bool HasEntry(string name) => Names.Contains(name);
    }

    private static Sniff? TrySniffZip(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bundle = new List<(string, string, long)>();
            var hasModsDir = false;
            foreach (var e in zip.Entries)
            {
                var n = e.FullName.Replace('\\', '/');
                if (n.Length == 0) continue;
                names.Add(n.TrimEnd('/'));
                if (n.StartsWith(ModsDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    hasModsDir = true;
                    if (!n.EndsWith('/') && e.Length > 0 &&
                        (n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                         || n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        bundle.Add((n, n[ModsDirPrefix.Length..], e.Length));
                    }
                }
            }
            return new Sniff
            {
                Names = names,
                BundleFiles = bundle,
                HasModsDir = hasModsDir,
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ImportPlan BuildMrpackPlan(string full, Sniff s, List<string> notes)
    {
        // mrpack 允许读索引（包自身契约，非 mod 代码语义）——直接在内存读条目，
        // 预览阶段绝不落盘（不复用 MrpackInstaller.ReadIndex，避免临时目录写入）。
        MrpackInstaller.Index? index = null;
        var declared = new List<ImportFile>();
        long total = 0;
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(MrpackMarker);
            if (entry is not null)
            {
                using var stream = entry.Open();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                var deps = root.Child("dependencies");
                string? loader = null;
                string? loaderVersion = null;
                foreach (var (key, name) in MrpackLoaderDeps)
                {
                    var v = deps.Child(key);
                    if (v.ValueKind == JsonValueKind.Undefined) continue;
                    loader = name;
                    loaderVersion = v.OptText();
                    break;
                }
                index = new MrpackInstaller.Index(
                    root.Child("name").Text(""),
                    root.Child("versionId").Text(""),
                    deps.Child("minecraft").OptText(),
                    loader, loaderVersion);
                foreach (var f in root.Child("files").Items())
                {
                    var p = f.Child("path").Text("");
                    if (p.Length == 0) continue;
                    declared.Add(new ImportFile(p.Replace('\\', '/'), null,
                        f.Child("size").Int(-1), false));
                    total += Math.Max(0, f.Child("size").Int(-1));
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            notes.Add("modrinth.index.json 存在但无法解析，按整合包处理时会在安装阶段报错");
        }
        if (index is not null)
        {
            return new ImportPlan
            {
                SourcePath = full,
                Kind = ImportKind.Mrpack,
                Confidence = ImportConfidence.Confirmed,
                Evidence = [$"根条目 {MrpackMarker} 命中"],
                SuggestedSubdir = ".（由索引 files[] 与 overrides/ 分发）",
                Files = declared,
                TotalBytes = total,
                DetectedLoader = index.Loader,
                DetectedGameVersion = index.McVersion,
                Notes =
                [
                    .. notes,
                    $"整合包「{index.Name} {index.VersionId}」：MC {index.McVersion ?? "?"}，加载器 {index.Loader ?? "?"}（{index.LoaderVersion ?? "?"}）；" +
                    $"索引声明 {declared.Count} 个模组文件；overrides/ 与 server-overrides/ 内容不在此清单内",
                ],
                Actionable = IsMrpackLoaderSupported(index.Loader),
            };
        }
        // 索引读不出来时仍按 mrpack 结构处理，但不保证可装
        return new ImportPlan
        {
            SourcePath = full,
            Kind = ImportKind.Mrpack,
            Confidence = ImportConfidence.Likely,
            Evidence = [$"根条目 {MrpackMarker} 命中（索引解析失败）"],
            SuggestedSubdir = ".（由索引 files[] 与 overrides/ 分发）",
            Files = declared,
            TotalBytes = total,
            Notes = ["整合包索引无法解析：安装阶段若仍失败请检查文件完整性", .. notes],
            Actionable = false,
        };
    }

    private static bool IsMultiMc(string full, Sniff s)
    {
        if (s.HasEntry("manifest.yml") || s.HasEntry("manifest-minecraft.json")) return true;
        if (!s.HasEntry("version.json")) return false;
        // 原版服务端 jar 的 version.json 没有 formatVersion 键（它是 {id,javaVersion,...}）；
        // MultiMC/PCL 实例包的 version.json 带 formatVersion。读一个纯数据键，不改判定哲学。
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry("version.json");
            if (entry is null) return false;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("formatVersion", out _);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            return false; // 解析不了 → 不归类，宁保守勿武断
        }
    }

    private static ImportPlan BuildMultiMcPlan(string full, Sniff s, List<string> notes)
    {
        var marker = MultiMcMarkers.FirstOrDefault(s.HasEntry) ?? MultiMcMarkers[0];
        // 走到这里说明顶层没有 mods/（有的话已先判成合集），可列内容即实例元数据本身
        var listed = s.BundleFiles
            .Select(b => new ImportFile(ModsDirPrefix + b.Relative, b.Entry, b.Bytes, false))
            .ToList();
        notes.Add("MultiMC/PCL 实例格式暂不完整支持（version.json 的实例级安装属 providers 范畴），" +
                  "此处仅列出可识别内容，不落盘");
        return new ImportPlan
        {
            SourcePath = full,
            Kind = ImportKind.MultiMcPack,
            Confidence = ImportConfidence.Confirmed,
            Evidence = [$"根条目 {marker} 命中（未检出 mods/ 目录；含 mods/ 的包会先按合集识别）"],
            SuggestedSubdir = "",
            Files = listed,
            TotalBytes = listed.Sum(f => f.Bytes),
            Notes = notes,
            Actionable = false,
        };
    }

    private static ImportPlan SingleJarPlan(string full, Sniff s, ImportKind kind, string marker,
        string? loader, string? gameVersion, List<string> notes)
    {
        var len = new FileInfo(full).Length;
        return new ImportPlan
        {
            SourcePath = full,
            Kind = kind,
            Confidence = ImportConfidence.Confirmed,
            Evidence = [$"条目 {marker} 命中"],
            SuggestedSubdir = "mods",
            Files = [new ImportFile("mods/" + Path.GetFileName(full), null, len, false)],
            TotalBytes = len,
            DetectedLoader = loader,
            DetectedGameVersion = gameVersion,
            Notes = notes,
            Actionable = true,
        };
    }

    private static ImportPlan BuildBundlePlan(string full, Sniff s, List<string> notes)
    {
        var files = s.BundleFiles
            .Select(b => new ImportFile(ModsDirPrefix + b.Relative, b.Entry, b.Bytes, false))
            .ToList();
        notes.Add("包内各 jar 未逐个嗅探加载器标记，落地时 mods/ 下的相对路径原样保留");
        return new ImportPlan
        {
            SourcePath = full,
            Kind = ImportKind.ModsBundle,
            Confidence = ImportConfidence.Confirmed,
            Evidence = [$"根条目 mods/ 命中，共 {files.Count} 个文件"],
            SuggestedSubdir = "mods",
            Files = files,
            TotalBytes = files.Sum(f => f.Bytes),
            Notes = notes,
            Actionable = true,
        };
    }

    // —— 内部：元数据轻读取（只取纯数据字段，不解释语义）——

    private static (string? Loader, string? Gv, string? Env, string? EnvNote) ReadFabricMeta(string full, Sniff s)
    {
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(FabricMarker);
            if (entry is null) return ("fabric", null, null, null);
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var gv = root.TryGetProperty("depends", out var dep)
                     && dep.ValueKind == JsonValueKind.Object
                     && dep.TryGetProperty("minecraft", out var mc)
                ? mc.OptText()
                : null;
            var env = root.TryGetProperty("environment", out var e2) ? e2.OptText() : null;
            string? envNote = env == "client"
                ? "fabric.mod.json 声明 environment=client：客户端专属模组，装到服务端无意义（导入会被匹配判定拦截）"
                : null;
            return ("fabric", gv, env, envNote);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            return ("fabric", null, null, null);
        }
    }

    private static string? ReadTomlMcVersion(string full, string marker)
    {
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(marker);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            // mods.toml：mod 条目的 mcVersion = "..." 或 minecraftVersion = "..."（范围写法保留原文，不做展开）
            var m = Regex.Match(text, "^(?:mcVersion|minecraftVersion)\\s*=\\s*\"([^\"]+)\"",
                RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static string? ReadLegacyForgeMcVersion(string full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(LegacyForgeMarker);
            if (entry is null) return null;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            // mcmod.info 是数组 [{...}]
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                var v = o.TryGetProperty("mcversion", out var mc) ? mc.OptText() : null;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    // —— 内部：匹配（保守比较，不确定一律 Unknown）——

    private static ImportPlan Finalize(ImportPlan plan, InstanceMeta? meta, string? instanceDir)
    {
        if (instanceDir is not null && plan.Files.Count > 0)
        {
            bool Exists(ImportFile f) => File.Exists(Path.Combine(instanceDir,
                f.RelativeTarget.Replace('/', Path.DirectorySeparatorChar)));
            var files = plan.Files.Select(f => f with { AlreadyExists = Exists(f) }).ToList();
            var exist = files.Where(f => f.AlreadyExists).Select(f => f.RelativeTarget).ToList();
            plan = plan with
            {
                Files = files,
                Notes = exist.Count > 0
                    ? [.. plan.Notes, "目标实例已有同名文件，确认后将覆盖: " + string.Join(", ", exist)]
                    : plan.Notes,
            };
        }

        if (meta is null || plan.Kind == ImportKind.Unknown || plan.Kind == ImportKind.MultiMcPack)
        {
            return plan; // MultiMc 的「不确定」由 Notes 表达，Match 保持 Unknown
        }

        if (plan.Kind == ImportKind.ServerJarCandidate)
        {
            return plan with
            {
                Match = MatchVerdict.Unknown,
                MatchReason = "用途未定（可能是服务端 jar），需用户指定后才判定",
            };
        }

        if (plan.Kind == ImportKind.Mrpack)
        {
            return plan with { Match = MatchMrpack(plan, meta, out var reason), MatchReason = reason };
        }

        // 单模组 jar / 合集：加载器与实例类型比对
        return plan with { Match = MatchModJar(plan, meta, out var r), MatchReason = r };
    }

    private static bool IsMrpackLoaderSupported(string? loader) =>
        loader is "fabric" or "neoforge"; // forge/quilt 暂不支持（spec 明示）

    private static MatchVerdict MatchMrpack(ImportPlan plan, InstanceMeta meta, out string reason)
    {
        if (plan.DetectedGameVersion is { } gv && !string.IsNullOrWhiteSpace(gv) &&
            gv != meta.GameVersion)
        {
            reason = $"整合包要求 MC {gv}，实例是 {meta.GameVersion}";
            return MatchVerdict.Mismatch;
        }
        var want = meta.ServerType.LoaderId();
        if (want is null)
        {
            reason = $"{meta.ServerType.ZhLabel()} 没有模组加载器，无法接收整合包";
            return MatchVerdict.Mismatch;
        }
        if (plan.DetectedLoader is { } dl && dl != want)
        {
            if (!IsMrpackLoaderSupported(dl))
            {
                reason = $"整合包加载器 {dl} 暂不支持（当前支持 fabric / neoforge）";
            }
            else
            {
                reason = $"实例加载器是 {want}，整合包要求 {dl}";
            }
            return MatchVerdict.Mismatch;
        }
        if (plan.DetectedGameVersion is null or "" || plan.DetectedLoader is null)
        {
            reason = "整合包未声明完整的版本/加载器信息，无法确证匹配";
            return MatchVerdict.Unknown;
        }
        reason = $"MC {meta.GameVersion} + {want} 与整合包声明一致";
        return MatchVerdict.Matches;
    }

    private static MatchVerdict MatchModJar(ImportPlan plan, InstanceMeta meta, out string reason)
    {
        // 客户端专属模组：ceobe 编排的全是服务端实例，environment=client 一律不匹配
        // （fabric 读 fabric.mod.json 的 environment；Modrinth 侧对应 version.environment=client_only）
        if (plan.DetectedEnvironment == "client")
        {
            reason = "模组声明 environment=client：客户端专属，本工具管理的是服务端实例，装了也不生效";
            return MatchVerdict.Mismatch;
        }

        var jarLoader = plan.DetectedLoader;
        if (jarLoader is null)
        {
            reason = "未识别出加载器标记，无法判定匹配";
            return MatchVerdict.Unknown;
        }

        // 加载器兼容矩阵（保守）：
        //   fabric   模组 → 仅 FABRIC 实例；其余实例 Mismatch
        //   neoforge 模组 → 仅 NEOFORGE 实例；其余实例 Mismatch
        //   forge    模组 → NEOFORGE 实例 Unknown（通常兼容但未实测）；FABRIC 实例 Mismatch
        // 地基 LoaderId() 只对 FABRIC/NEOFORGE 返回非 null；PAPER/PURPUR/VANILLA 返回 null → Mismatch。
        var serverLoader = meta.ServerType.LoaderId();
        if (serverLoader is null)
        {
            reason = $"目标实例（{meta.ServerType.ZhLabel()}）没有模组加载器";
            return MatchVerdict.Mismatch;
        }
        if (jarLoader == "forge")
        {
            if (serverLoader == "fabric")
            {
                reason = "Forge 模组不能装在 Fabric 实例上";
                return MatchVerdict.Mismatch;
            }
            // neoforge：不武断宣称兼容
            reason = "Forge 模组在 NeoForge 上通常兼容，但未实测，按不确定处理";
            return MatchVerdict.Unknown;
        }
        if (jarLoader != serverLoader)
        {
            reason = $"{jarLoader} 模组需要 {jarLoader} 实例，当前实例加载器为 {serverLoader}";
            return MatchVerdict.Mismatch;
        }

        // 版本字段：只有「精确相等」才敢给 Matches；范围/通配/缺失一律 Unknown
        var gv = plan.DetectedGameVersion;
        if (string.IsNullOrWhiteSpace(gv) || gv == "*" ||
            gv.Contains(',') || gv.Contains('[') || gv.Contains('(') || gv.StartsWith('~') ||
            gv.StartsWith('^') || gv.Contains('-'))
        {
            reason = $"加载器匹配；游戏版本约束「{(string.IsNullOrWhiteSpace(gv) ? "未声明" : gv)}」无法与 {meta.GameVersion} 精确比对";
            return MatchVerdict.Unknown;
        }
        if (gv != meta.GameVersion)
        {
            reason = $"模组声明 MC {gv}，实例是 {meta.GameVersion}";
            return MatchVerdict.Mismatch;
        }
        reason = $"加载器与 MC 版本（{meta.GameVersion}）均与实例一致";
        return MatchVerdict.Matches;
    }

    // —— 内部：合集落地 ——

    private static async Task<ImportResult> InstallBundleAsync(ImportPlan plan, string fullInstance,
        IProgressSink sink, CancellationToken ct)
    {
        var modsDir = Path.Combine(fullInstance, "mods");
        var records = new List<ModEntry>();
        var written = 0;
        long bytes = 0;
        try
        {
            using var zip = ZipFile.OpenRead(plan.SourcePath);
            foreach (var f in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (f.SourceEntry is null) continue;
                var entry = zip.GetEntry(f.SourceEntry);
                if (entry is null) continue;
                var target = Path.GetFullPath(Path.Combine(fullInstance,
                    f.RelativeTarget.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(target, fullInstance)) continue; // 防穿越（ZipUtil.Extract 之外的第二道闸）
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                sink.Update("复制模组 " + Path.GetFileName(target), written, plan.Files.Count);
                await using (var src = entry.Open())
                await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write,
                                 FileShare.None, 64 * 1024, useAsync: true))
                {
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
                bytes += entry.Length;
                written++;
                records.Add(MakeRecord(Path.GetFileName(target), target, plan.DetectedLoader));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new CeobeException("导入 mod 合集失败: " + plan.SourcePath, e);
        }
        var msg = $"合集导入完成：{written} 个文件进入 mods/";
        sink.Update(msg, written, written);
        return new ImportResult(plan.Kind, written, bytes, records, msg);
    }

    private static ModEntry MakeRecord(string fileName, string targetPath, string? loader)
    {
        string sha1;
        try { sha1 = HttpUtil.Sha1(targetPath); }
        catch (CeobeException) { sha1 = ""; }
        return new ModEntry
        {
            ProjectId = "",
            Slug = Path.GetFileNameWithoutExtension(fileName),
            Title = Path.GetFileNameWithoutExtension(fileName),
            VersionNumber = "",
            FileName = fileName,
            Sha1 = sha1,
            EnvServer = "optional",
            EnvClient = "optional",
            InstalledAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool IsWithin(string candidate, string root)
    {
        var c = TrimSep(candidate);
        var r = TrimSep(root);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.Equals(r, cmp) || c.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }

    private static string TrimSep(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
