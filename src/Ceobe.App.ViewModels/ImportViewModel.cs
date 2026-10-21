using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Mods;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;
using static Ceobe.Core.Mods.ModImporter;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「一键导入」确认页 ViewModel（整改项 4）。
///
/// 训练员原话：「mod下载和整合包下载我没看懂怎么用，由于pcl的生态已经非常完善且便利，
/// 我的建议是可以把它设置成一键导入的模式，mod接受对应的jar文件，整合包接受对应的压缩包文件」。
///
/// 设计原则：**识别 → 预览 → 确认才落盘，绝不静默猜测**。
/// 静默猜测的代价是污染实例目录——一旦把整合包误当 mod 塞进 mods/，
/// 用户要么启动崩溃，要么得手工去目录里翻几十个文件删干净。故：
///
/// - <see cref="InspectAsync"/> 只读，绝不写盘，产出完整预览（判定依据/落位/清单/匹配结论）；
/// - <see cref="CanInstall"/> 是唯一放行闸门：<c>Actionable</c>（Core 判定可自动导入）
///   且匹配不是 Mismatch，除非用户显式勾了 <see cref="AllowMismatch"/>；
/// - 识别不确定（Ambiguous / ServerJarCandidate / Unknown）时不给「导入」按钮，
///   而是把 Core 的判定依据原样摊开，让用户自己决定这个文件到底是什么。
/// </summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly string _instance;
    private readonly string _dir;

    /// <summary>预览的文件清单（最多显示 <see cref="MaxPreviewFiles"/> 条，其余折叠计数）。</summary>
    public ObservableCollection<ImportFileRow> Files { get; } = [];

    /// <summary>判定依据（Core 的 Evidence：命中了哪些包内标记）。</summary>
    public ObservableCollection<string> Evidence { get; } = [];

    /// <summary>附加提示（可能缺失依赖 / 暂不完整支持 / 重名冲突等）。</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>界面最多列出的文件数：整合包可能有几百个 mod，全列出来等于没预览。</summary>
    private const int MaxPreviewFiles = 40;

    [ObservableProperty]
    private string? _sourcePath;

    /// <summary>识别出的种类（中文标签见 <see cref="KindLabel"/>）。</summary>
    [ObservableProperty]
    private ImportKind? _kind;

    [ObservableProperty]
    private ImportConfidence _confidence;

    /// <summary>建议落位（如 <c>mods/</c>）；用户必须能看见文件会去哪。</summary>
    [ObservableProperty]
    private string _suggestedSubdir = "";

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private long _totalBytes;

    /// <summary>已存在同名文件的数量（会被覆盖，必须提前告知）。</summary>
    [ObservableProperty]
    private int _overwriteCount;

    [ObservableProperty]
    private string? _detectedLoader;

    [ObservableProperty]
    private string? _detectedGameVersion;

    [ObservableProperty]
    private string? _detectedEnvironment;

    [ObservableProperty]
    private MatchVerdict _match = MatchVerdict.Unknown;

    [ObservableProperty]
    private string _matchReason = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>是否正在识别或安装（禁用按钮，防止重复点击）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Core 判定可否自动导入（MultiMcPack / ServerJarCandidate / Unknown 为 false）。</summary>
    [ObservableProperty]
    private bool _actionable;

    /// <summary>用户显式勾选「强行导入」（匹配不符时唯一的放行方式）。</summary>
    [ObservableProperty]
    private bool _allowMismatch;

    /// <summary>是否已完成识别（未识别前不给安装按钮）。</summary>
    [ObservableProperty]
    private bool _inspected;

    /// <summary>进度文本（安装整合包时会下载几十个 mod）。</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>当前识别计划；<see cref="InstallAsync"/> 只接受它，不接受路径。</summary>
    private ImportPlan? _plan;

    public ImportViewModel(InstanceManager manager, string instance)
    {
        _manager = manager;
        _instance = instance;
        _dir = manager.Dir(instance);
    }

    /// <summary>识别结果的中文标签。</summary>
    public string KindLabel => Kind switch
    {
        ImportKind.Mrpack => "Modrinth 整合包（.mrpack）",
        ImportKind.FabricMod => "Fabric 模组",
        ImportKind.NeoForgeMod => "NeoForge 模组",
        ImportKind.ForgeMod => "Forge 模组",
        ImportKind.LegacyForgeMod => "旧版 Forge 模组（MC ≤1.12）",
        ImportKind.ModsBundle => "模组合集压缩包",
        ImportKind.MultiMcPack => "MultiMC / PCL 实例包",
        ImportKind.ServerJarCandidate => "疑似服务端 jar",
        ImportKind.Unknown => "无法识别",
        null => "（尚未识别）",
        _ => Kind.ToString()!,
    };

    /// <summary>置信度标签。Ambiguous 必须显眼提示「需要你确认」。</summary>
    public string ConfidenceLabel => Confidence switch
    {
        ImportConfidence.Confirmed => "判定确定",
        ImportConfidence.Likely => "倾向性判定",
        ImportConfidence.Ambiguous => "需要你确认用途",
        _ => "",
    };

    /// <summary>匹配结论标签（保守原则：拿不准一律说「不确定」，绝不乐观报「匹配」）。</summary>
    public string MatchLabel => Match switch
    {
        MatchVerdict.Matches => "与当前实例匹配 ✓",
        MatchVerdict.Mismatch => "与当前实例不匹配",
        MatchVerdict.Unknown => "无法确认是否匹配",
        _ => "",
    };

    /// <summary>
    /// 能否点「导入」。这是唯一放行闸门：
    /// 必须已识别、Core 判定可自动导入，且不匹配时须用户显式勾选强行导入。
    /// </summary>
    public bool CanInstall => Inspected && Actionable && (Match != MatchVerdict.Mismatch || AllowMismatch);

    /// <summary>
    /// 不可导入时给用户的下一步指引。宁可说清「为什么不给你按钮」，
    /// 也不要把按钮置灰了事——置灰而不解释等于让用户自己猜。
    /// </summary>
    public string BlockedReason => Kind switch
    {
        ImportKind.MultiMcPack
            => "MultiMC / PCL 实例包暂不完整支持：上面已列出可识别内容，" +
               "但实例级安装（版本/加载器解析）不在本功能范围。可改用「整合包」页签搜索 Modrinth 版本。",
        ImportKind.ServerJarCandidate
            => "这个 jar 里没有任何 mod 标记（fabric.mod.json / mods.toml 等），" +
               "疑似是服务端本体。若确实要替换服务端，请改用「服务器配置」里的启动 jar 设置，" +
               "不要塞进 mods/（塞进去会导致启动崩溃）。",
        ImportKind.Unknown
            => "读不出这个文件的内容（不是有效的 zip/jar，或文件损坏）。" +
               "请确认文件完整，或告诉我它的具体格式以便支持。",
        _ when !Inspected => "请先选择要导入的文件",
        _ when Match == MatchVerdict.Mismatch && !AllowMismatch
            => MatchReason + "（确认无误可勾选「强行导入」）",
        _ => "",
    };

    partial void OnAllowMismatchChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnActionableChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnInspectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(BlockedReason));
    }
    partial void OnKindChanged(ImportKind? value) => OnPropertyChanged(nameof(BlockedReason));
    partial void OnMatchChanged(MatchVerdict value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(MatchLabel));
    }

    /// <summary>
    /// 识别一个本地文件（只读，绝不写盘）。支持 .jar / .mrpack / .zip。
    /// 拖拽与「浏览文件」都走这里。
    /// </summary>
    [RelayCommand]
    private async Task InspectAsync(string? path, CancellationToken ct)
    {
        ClearPreview();
        var candidate = (path ?? SourcePath)?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            HasError = true;
            StatusText = "请选择要导入的文件（.jar / .mrpack / .zip）";
            return;
        }
        if (!File.Exists(candidate))
        {
            HasError = true;
            StatusText = "文件不存在: " + candidate;
            return;
        }

        IsBusy = true;
        StatusText = "正在识别…";
        try
        {
            // 带上 meta 与实例目录：Core 才能判断加载器/版本是否匹配、哪些文件会被覆盖
            var meta = _manager.Find(_instance)?.Meta;
            var plan = await ModImporter.InspectAsync(candidate, meta, _dir, ct).ConfigureAwait(true);
            ApplyPlan(plan);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "识别失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把 Core 的识别结果投影成界面可绑定的形态。</summary>
    private void ApplyPlan(ImportPlan plan)
    {
        _plan = plan;
        SourcePath = plan.SourcePath;
        Kind = plan.Kind;
        Confidence = plan.Confidence;
        SuggestedSubdir = string.IsNullOrEmpty(plan.SuggestedSubdir) ? "(实例根目录)" : plan.SuggestedSubdir;
        DetectedLoader = plan.DetectedLoader;
        DetectedGameVersion = plan.DetectedGameVersion;
        DetectedEnvironment = plan.DetectedEnvironment;
        Match = plan.Match;
        MatchReason = plan.MatchReason;
        Actionable = plan.Actionable;

        FileCount = plan.Files.Count;
        TotalBytes = plan.Files.Sum(f => f.Bytes);
        OverwriteCount = plan.Files.Count(f => f.AlreadyExists);

        Files.Clear();
        foreach (var f in plan.Files.Take(MaxPreviewFiles))
        {
            Files.Add(new ImportFileRow(f.RelativeTarget, f.Bytes, f.AlreadyExists));
        }

        Evidence.Clear();
        foreach (var e in plan.Evidence) Evidence.Add(e);

        Notes.Clear();
        foreach (var n in plan.Notes) Notes.Add(n);
        // 覆盖提示必须进 Notes：这是唯一会让用户已有文件消失的情况
        if (OverwriteCount > 0)
        {
            Notes.Insert(0, $"{OverwriteCount} 个同名文件将被覆盖（下面标 ⚠ 的就是）");
        }
        if (plan.Files.Count > MaxPreviewFiles)
        {
            Notes.Add($"另有 {plan.Files.Count - MaxPreviewFiles} 个文件未列出");
        }

        Inspected = true;
        HasError = false;
        StatusText = BuildSummary(plan);
    }

    /// <summary>预览摘要：一句话说清「这是什么、会写几个文件到哪、多大」。</summary>
    private string BuildSummary(ImportPlan plan)
    {
        var size = HumanSize(plan.Files.Sum(f => f.Bytes));
        var where = string.IsNullOrEmpty(plan.SuggestedSubdir) ? "实例根目录" : plan.SuggestedSubdir + "/";
        return $"识别为{KindLabel}：{plan.Files.Count} 个文件、{size} → {where}";
    }

    /// <summary>
    /// 执行导入。必须先 <see cref="InspectAsync"/>，且 <see cref="CanInstall"/> 为真。
    /// 安装成功后把 mod 记录写回 instance.json（走 Core 的 AppendMods，与 CLI 共用一套逻辑）。
    /// </summary>
    [RelayCommand]
    private async Task InstallAsync(CancellationToken ct)
    {
        var plan = _plan;
        if (plan is null)
        {
            HasError = true;
            StatusText = "请先选择文件并完成识别";
            return;
        }
        if (!CanInstall)
        {
            HasError = true;
            StatusText = BlockedReason;
            return;
        }
        if (_manager.Find(_instance)?.State.IsLive() == true)
        {
            // 运行中往 mods/ 塞文件：多数加载器在启动时就锁定了 mod 清单，
            // 热塞进去不生效，还可能撞上文件锁。宁可拦下让用户重启。
            HasError = true;
            StatusText = "实例正在运行，请先停止再导入（运行中改 mods/ 不会生效，且可能撞上文件锁）。";
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new VmProgress(this);
            var result = await ModImporter.InstallAsync(plan, _dir, AllowMismatch, progress, ct)
                .ConfigureAwait(true);

            // mod 记录写回 instance.json：与 CLI 共用 Core 的 AppendMods，避免两套逻辑漂移
            if (result.Records.Count > 0)
            {
                _manager.AppendMods(_instance, result.Records);
            }

            HasError = false;
            StatusText = $"导入完成：{result.WrittenFiles} 个文件（{HumanSize(result.WrittenBytes)}）"
                         + (result.Records.Count > 0 ? $"，登记 {result.Records.Count} 个 mod" : "")
                         + "。" + result.Message;
            // 导入后清空预览：文件已落盘，留着旧清单会让人以为还没导
            ClearPreview();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "导入失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    /// <summary>
    /// 用户主动清空：预览与提示一起清。
    /// 与 <see cref="ClearPreview"/> 分开是因为「点了清空却还留着『导入完成』」
    /// 会让界面自相矛盾——预览区空了，状态栏却在报上一次操作的结果。
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        ClearPreview();
        HasError = false;
        StatusText = "";
    }

    /// <summary>
    /// 只清预览状态，不碰 <see cref="StatusText"/> 与 <see cref="HasError"/>。
    /// 供 <see cref="InspectAsync"/> / <see cref="InstallAsync"/> 内部调用：
    /// 它们紧接着要写自己的结果消息，清掉反而把反馈抹了
    /// （早期版本在这里按文案内容反推要不要清空，结果把「导入完成」整条吃掉）。
    /// </summary>
    private void ClearPreview()
    {
        _plan = null;
        Files.Clear();
        Evidence.Clear();
        Notes.Clear();
        Kind = null;
        Confidence = ImportConfidence.Ambiguous;
        SuggestedSubdir = "";
        FileCount = 0;
        TotalBytes = 0;
        OverwriteCount = 0;
        DetectedLoader = null;
        DetectedGameVersion = null;
        DetectedEnvironment = null;
        Match = MatchVerdict.Unknown;
        MatchReason = "";
        Actionable = false;
        AllowMismatch = false;
        Inspected = false;
        ProgressText = "";
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(BlockedReason));
    }

    /// <summary>文件扩展名是否是本功能支持的输入（供拖拽过滤，避免拖个 .txt 进来）。</summary>
    public static bool IsSupportedFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jar" or ".mrpack" or ".zip";
    }

    /// <summary>体积格式化，委托给公共工具（导入页与设置页搬家预览共用同一实现）。</summary>
    public static string HumanSize(long bytes) => TextFormat.HumanSize(bytes);

    /// <summary>把 Core 的进度回调接到界面文本（整合包要下载几十个 mod，必须有进度）。</summary>
    private sealed class VmProgress(ImportViewModel vm) : IProgressSink
    {
        private string? _last;

        public void Update(string stage, long done, long total) => UiThread.Post(() =>
        {
            // 节流：同一 stage 只更新一次，避免 UI 线程被字节级进度刷爆
            if (stage == _last) return;
            _last = stage;
            vm.ProgressText = stage;
        });
    }
}

/// <summary>预览清单的一行。</summary>
/// <param name="Path">相对实例目录的落位路径</param>
/// <param name="Bytes">文件大小</param>
/// <param name="Overwrites">是否会覆盖已有同名文件</param>
public sealed record ImportFileRow(string Path, long Bytes, bool Overwrites)
{
    public string Size => ImportViewModel.HumanSize(Bytes);
}
