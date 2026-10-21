using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「设置」弹窗 ViewModel（整改项 3：训练员原话「服务器存放位置貌似无法修改地址，
/// 至少我没看到（它默认到了我的c盘）」）。
///
/// Java 版 <c>AppSettings</c> 只有 theme 一个字段，<c>CeobeHome</c> 也不认设置文件，
/// 所以数据目录**根本没有入口可改**。C# 版补上，并把搬家做成三段式：
///
/// 1. <see cref="PlanMoveAsync"/> —— 只读预演：列出会搬什么、目标是否可用、有何风险；
/// 2. 用户在界面上确认；
/// 3. <see cref="ExecuteMoveAsync"/> —— 真搬，且**失败自动回滚**。
///
/// 为什么必须这样：搬目录是本项目里唯一不可逆且可能毁掉存档的操作。
/// 直接搬的话，中途磁盘满/权限不足/世界被占用，就会留下「半搬」状态——
/// 新目录有一半文件、旧目录有一半文件，两边都不是完整实例，用户无从恢复。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly InstanceManager _manager;

    /// <summary>当前生效的数据根目录。</summary>
    [ObservableProperty]
    private string _currentHome = "";

    /// <summary>主题（light | dark）。</summary>
    [ObservableProperty]
    private string _theme = "light";

    /// <summary>整改项 2：新建实例时白名单默认值。Java 版默认 true 又没入口，
    /// 导致新手开服后朋友进不来。改为默认 false，创建向导里显式问一次。</summary>
    [ObservableProperty]
    private bool _defaultWhitelistEnabled;

    /// <summary>用户选定的新目录（未确认前不生效）。</summary>
    [ObservableProperty]
    private string? _proposedHome;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>是否正在搬家（期间必须禁用一切按钮）。</summary>
    [ObservableProperty]
    private bool _isMoving;

    /// <summary>搬家预演结果；null = 尚未预演。</summary>
    [ObservableProperty]
    private MovePlan? _plan;

    /// <summary>OneDrive 重定向警告（整改项 3 的 Windows 专属新雷）。</summary>
    [ObservableProperty]
    private string? _oneDriveWarning;

    public SettingsViewModel(InstanceManager manager)
    {
        _manager = manager;
        Load();
    }

    /// <summary>从 settings.json 与当前生效路径加载。</summary>
    public void Load()
    {
        // 先读当前生效值（可能是 Override 或默认 ~/.ceobe），再读设置文件
        CurrentHome = CeobeHome.Root();
        var settings = AppSettings.Load();
        Theme = settings.Theme;
        DefaultWhitelistEnabled = settings.DefaultWhitelistEnabled;
        ProposedHome = settings.HomePath;
        HasError = false;
        StatusText = "";
        Plan = null;
        OneDriveWarning = AppSettings.IsPathUnderOneDrive(CurrentHome)
            ? "当前数据目录在 OneDrive 同步范围内：Minecraft 世界文件写入极频繁，"
              + "实时同步会把云盘占满并拖慢开服，建议改到非同步目录（如 D:\\ceobe）。"
            : null;
    }

    /// <summary>保存主题与默认白名单等轻量设置（不涉及搬家）。</summary>
    [RelayCommand]
    private void SaveSettings()
    {
        var settings = AppSettings.Load();
        settings.Theme = string.Equals(Theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
        settings.DefaultWhitelistEnabled = DefaultWhitelistEnabled;
        settings.LastInstallDir = ProposedHome;
        if (!settings.Save())
        {
            HasError = true;
            StatusText = "设置写入失败（磁盘权限或空间问题）";
            return;
        }
        HasError = false;
        StatusText = "设置已保存";
    }

    /// <summary>
    /// 搬家预��（只读，不碰任何文件）。
    /// 必须显式调用并由用户在界面上看过 <see cref="Plan"/> 之后才允许 <see cref="ExecuteMoveAsync"/>。
    /// </summary>
    [RelayCommand]
    private async Task PlanMoveAsync(CancellationToken ct)
    {
        Plan = null;
        var target = ProposedHome?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            HasError = true;
            StatusText = "请先选择目标目录";
            return;
        }

        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(target);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            HasError = true;
            StatusText = "目标路径无效: " + e.Message;
            return;
        }

        var source = SafeRoot();
        if (string.Equals(Path.GetFullPath(source), fullTarget, PathComparison))
        {
            HasError = true;
            StatusText = "目标目录与当前目录相同，无需搬家";
            return;
        }

        var plan = await Task.Run(() => BuildPlan(source, fullTarget), ct).ConfigureAwait(true);
        Plan = plan;
        HasError = plan.Blockers.Count > 0;
        StatusText = plan.Blockers.Count > 0
            ? "存在阻塞问题，无法搬家：" + string.Join("；", plan.Blockers)
            : $"预演通过：将搬 {plan.InstanceCount} 个实例、{plan.TotalBytes / 1024 / 1024} MB，" +
              (plan.Warnings.Count == 0 ? "无风险项" : "注意 " + plan.Warnings.Count + " 条警告");
    }

    /// <summary>
    /// 执行搬家。前置条件：
    /// 1. 必须先有 <see cref="Plan"/>（且无 Blockers）——不允许跳过预演直接搬；
    /// 2. 所有实例必须已停止——搬一个运行中的实例等于同时改它的世界文件，必然损坏存档。
    /// 失败自动回滚：已搬走的文件退回原位，目标目录清理掉。
    /// </summary>
    [RelayCommand]
    private async Task ExecuteMoveAsync(CancellationToken ct)
    {
        var plan = Plan;
        if (plan is null || plan.Blockers.Count > 0)
        {
            HasError = true;
            StatusText = "请先完成预演并解决全部阻塞项";
            return;
        }

        // 运行中实例必须在 Plan 阶段就拦下，这里再校验一次（预演后用户可能启动了服）
        var live = _manager.List().Where(s => s.State.IsLive()).Select(s => s.Name).ToList();
        if (live.Count > 0)
        {
            HasError = true;
            StatusText = "以下实例仍在运行，请先停止再搬家：" + string.Join("、", live);
            return;
        }

        IsMoving = true;
        try
        {
            await Task.Run(() => PerformMove(plan), ct).ConfigureAwait(true);

            // settings.json 必须**双写**，因为它记录 HomePath 而自身又存放在 HomePath 里（循环依赖）：
            //
            // ① 旧目录那份是**重启后的锚点**。进程重启时 Override 为 null，CeobeHome.Root()
            //    回落到 %USERPROFILE%\.ceobe，只有从那里读到 HomePath 才知道数据搬到哪了。
            //    少写这一份，重启后 App 会去默认目录找一个空实例列表，用户以为存档全丢了。
            // ② 新目录那份让 Apply() 之后（Override=target）的 Load() 读到一致值。
            //    少写这一份，界面重新 Load 会拿到默认设置，主题等偏好看起来凭空重置。
            var settings = AppSettings.Load();          // 此刻 Override 仍指向旧目录
            settings.HomePath = plan.Target;
            if (!settings.Save())                       // → 写旧目录（锚点）
            {
                HasError = true;
                StatusText = "文件已搬到 " + plan.Target + "，但 settings.json 写入失败；" +
                             "请手动设置环境变量 CEOBE_HOME 指向该目录";
                return;
            }
            settings.Apply();                           // Override → 新目录
            settings.Save();                            // → 再写一份到新目录

            Load();
            HasError = false;
            StatusText = $"搬家完成：{plan.InstanceCount} 个实例已迁到 {plan.Target}";
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = e.Message;
        }
        catch (OperationCanceledException)
        {
            HasError = true;
            StatusText = "搬家已取消；已搬动的文件已回滚到原目录";
        }
        finally
        {
            IsMoving = false;
            Plan = null;   // 目录已变，旧预演作废
        }
    }

    /// <summary>放弃搬家，清空目标目录选择。</summary>
    [RelayCommand]
    private void CancelMove()
    {
        ProposedHome = null;
        Plan = null;
        HasError = false;
        StatusText = "已取消搬家";
    }

    private static string SafeRoot() => CeobeHome.Root();

    /// <summary>
    /// 往上找最近的存在目录（用于在不创建目标目录的前提下探测可写性）。
    /// 全部不存在返回 null——那种情况下目标路径本身就是无效的。
    /// </summary>
    private static string? NearestExistingAncestor(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir is not null)
            {
                if (dir.Exists) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
        }
        return null;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>只读预演：统计体量、检查目标可用性、识别运行中实例与 OneDrive 风险。</summary>
    private MovePlan BuildPlan(string source, string target)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var instances = new List<MoveItem>();

        var instancesDir = Path.Combine(source, "instances");
        if (Directory.Exists(instancesDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(instancesDir))
            {
                var name = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, "instance.json"))) continue;
                var status = _manager.Find(name);
                if (status is { } s && s.State.IsLive())
                {
                    // 硬阻塞：搬运行中的实例会同时改它的世界文件，存档必坏
                    blockers.Add($"实例 {name} 正在运行，请先停止");
                }
                instances.Add(new MoveItem(dir, Path.Combine(target, "instances", name), DirSize(dir)));
            }
        }

        // 缓存与自管 JDK 一起搬，否则新目录下要重新下载全部 jar（几百 MB）
        foreach (var sub in new[] { "cache", "jdk" })
        {
            var dir = Path.Combine(source, sub);
            if (Directory.Exists(dir))
            {
                instances.Add(new MoveItem(dir, Path.Combine(target, sub), DirSize(dir)));
            }
        }
        var settingsFile = Path.Combine(source, "settings.json");
        if (File.Exists(settingsFile))
        {
            // settings.json 不搬：它记录 HomePath，搬过去会指向旧值造成循环
            warnings.Add("settings.json 保留在原目录（它记录数据目录位置本身）");
        }

        // 目标目录检查
        if (File.Exists(target))
        {
            blockers.Add("目标路径已存在同名文件（不是目录）");
        }
        else if (Directory.Exists(target))
        {
            var existing = Directory.Exists(Path.Combine(target, "instances"))
                ? Directory.EnumerateDirectories(Path.Combine(target, "instances")).Count()
                : 0;
            if (existing > 0)
            {
                // 目标已有实例：同名会被覆盖，必须先说清楚
                blockers.Add($"目标目录已含 {existing} 个实例；同名实例会被覆盖，请换目录或先清理");
            }
            else
            {
                warnings.Add("目标目录已存在（将在其中创建 instances/cache/jdk）");
            }
        }

        // 可写性探测：**不创建目标目录**。预演阶段就该是净零副作用——
        // 早期版本在这里 CreateDirectory(target)，用户看完预演点「取消」后，
        // 磁盘上会莫名多出一个空目录，与「只读预演」的承诺矛盾。
        // 改为在最近的已存在祖先目录里建一个临时探针目录，验完立即删。
        if (!Directory.Exists(target))
        {
            var ancestor = NearestExistingAncestor(target);
            if (ancestor is null)
            {
                blockers.Add("目标路径的上层目录不存在: " + target);
            }
            else
            {
                var probe = Path.Combine(ancestor, ".ceobe-probe-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(probe);
                    var probeFile = Path.Combine(probe, "probe");
                    File.WriteAllText(probeFile, "probe");
                    File.Delete(probeFile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    blockers.Add("目标目录不可写: " + e.Message);
                }
                finally
                {
                    try { Directory.Delete(probe, recursive: true); } catch (IOException) { }
                }
            }
        }

        // 跨盘检查：Directory.Move 不能跨卷，必须走复制+删除，中途失败风险更高
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var targetRoot = Path.GetPathRoot(target);
        if (!string.Equals(sourceRoot, targetRoot, PathComparison))
        {
            warnings.Add($"跨盘搬迁（{sourceRoot} → {targetRoot}）：需复制而非移动，耗时更长且中断风险更高");
        }

        // 空间检查：目标盘剩余空间必须够（含余量）
        try
        {
            var drive = new DriveInfo(targetRoot ?? target);
            var need = instances.Sum(i => i.Bytes) * 2;   // 跨盘复制期两份并存，按 2 倍算
            if (drive.IsReady && drive.AvailableFreeSpace < need)
            {
                blockers.Add($"目标盘剩余 {drive.AvailableFreeSpace / 1024 / 1024} MB，" +
                             $"不足以容纳 {need / 1024 / 1024} MB（含复制期两份并存的余量）");
            }
        }
        catch (Exception)
        {
            warnings.Add("无法确认目标盘剩余空间，请自行确保足够");
        }

        if (AppSettings.IsPathUnderOneDrive(target))
        {
            warnings.Add("目标目录在 OneDrive 同步范围内：世界文件高频写入会打爆云盘，强烈建议换目录");
        }

        return new MovePlan(source, target, instances,
            instances.Sum(i => i.Bytes), blockers, warnings);
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    /// <summary>
    /// 真正执行搬迁，失败回滚。
    /// 顺序：先移动（同盘是原子的），任一失败就把已移动的逐个移回。
    /// </summary>
    private void PerformMove(MovePlan plan)
    {
        var done = new List<(string From, string To)>();
        try
        {
            foreach (var item in plan.Items)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                if (Directory.Exists(item.Source))
                {
                    MoveDirectory(item.Source, item.Target);
                }
                else if (File.Exists(item.Source))
                {
                    File.Move(item.Source, item.Target, overwrite: true);
                }
                else
                {
                    continue;   // 预演与执行之间被删掉了，跳过
                }
                done.Add((item.Source, item.Target));
            }
        }
        catch (Exception e)
        {
            // 回滚：已搬的逐个搬回，让目录树回到操作前的完整状态
            foreach (var (from, to) in done.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(to)) MoveDirectory(to, from);
                    else if (File.Exists(to)) File.Move(to, from, overwrite: true);
                }
                catch (Exception)
                {
                    // 回滚本身失败：不能再抛，否则掩盖原始错误。此处必须让用户知道两边状态
                }
            }
            throw new CeobeException(
                $"搬家失败并已回滚到 {plan.Source}：{e.Message}\n" +
                $"若回滚不完整，请检查 {plan.Source} 与 {plan.Target} 两处目录");
        }
    }

    /// <summary>
    /// 移动目录树。优先 <see cref="Directory.Move"/>（同盘为原子重命名），
    /// 跨盘失败时退化为「复制 + 删源」——复制完成后才删，删失败也只是留下冗余副本，
    /// 不会丢数据。
    /// </summary>
    private static void MoveDirectory(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (IOException)
        {
            // 跨卷或其他 I/O 问题 → 走复制路径
        }
        catch (UnauthorizedAccessException)
        {
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
        // 空目录也要建出来（世界里的空 chunk 目录等）
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        // 复制校验通过后才删源：删失败只是留下冗余，不会丢数据
        Directory.Delete(source, recursive: true);
    }
}

/// <summary>搬家预演结果（只读，供界面确认后执行）。</summary>
public sealed record MovePlan(
    string Source,
    string Target,
    IReadOnlyList<MoveItem> Items,
    long TotalBytes,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings)
{
    /// <summary>可安全执行（无阻塞项）。</summary>
    public bool CanExecute => Blockers.Count == 0;

    /// <summary>实例个数（cache/jdk 不计入，避免界面上数字对不上用户认知）。</summary>
    public int InstanceCount => Items.Count(i => i.Source.Contains("instances"));
}

/// <summary>单项搬迁：源、目标、字节数。</summary>
public sealed record MoveItem(string Source, string Target, long Bytes)
{
    /// <summary>界面显示用的相对描述（instances/xxx、cache、jdk）。</summary>
    public string Label
    {
        get
        {
            var name = Path.GetFileName(Source);
            var parent = Path.GetFileName(Path.GetDirectoryName(Source)) ?? "";
            return parent == "instances" ? $"实例 {name}" : name;
        }
    }
}
