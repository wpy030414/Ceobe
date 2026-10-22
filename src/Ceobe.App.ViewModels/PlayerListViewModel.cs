using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Config;
using Ceobe.Core.Model;
using Ceobe.Core.Players;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「玩家」页签 ViewModel（整改项 2：训练员原话「白名单默认开启，但没有入口填写白名单，
/// 只能在控制台输入指令，建议给玩家页面单开一页」）。
///
/// 本页把四个名单文件（whitelist / ops / banned-players / banned-ips）做成可编辑界面，
/// 并处理两条容易静默失败的语义：
///
/// 1. **写文件 ≠ 生效**。服务端在内存里缓存名单：whitelist 有 <c>whitelist reload</c>，
///    ops 与封禁表**没有** reload 命令，只能靠 op/deop/pardon 命令或重启。
///    故每个写操作都返回「还需要下发什么命令」，由本 VM 经控制通道执行；
///    执行不了就如实告诉用户「文件已改但服务端未生效，请重启」，绝不谎称已生效。
///
/// 2. **离线模式的 UUID**。online-mode=false 时服务端按玩家名算 UUID，
///    白名单里的 UUID 算错 → 文件被接受但玩家照样进不来。故加人时自动按
///    <see cref="OfflinePlayerUuid"/> 补齐，并把该 id 显示出来供核对。
/// </summary>
public partial class PlayerListViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly string _instance;
    private readonly string _dir;

    public ObservableCollection<WhitelistRow> Whitelist { get; } = [];
    public ObservableCollection<OpRow> Ops { get; } = [];
    public ObservableCollection<BanRow> BannedPlayers { get; } = [];
    public ObservableCollection<IpBanRow> BannedIps { get; } = [];

    /// <summary>待添加的玩家名输入框绑定。</summary>
    [ObservableProperty]
    private string _newPlayerName = "";

    /// <summary>待添加的 OP 权限等级（1..4）。</summary>
    [ObservableProperty]
    private int _newOpLevel = 4;

    /// <summary>待封禁的 IP 输入框绑定。</summary>
    [ObservableProperty]
    private string _newIp = "";

    /// <summary>server.properties 的 white-list 开关（本页可直接改）。</summary>
    [ObservableProperty]
    private bool _whitelistEnabled;

    /// <summary>enforce-whitelist 开关。</summary>
    [ObservableProperty]
    private bool _enforceWhitelist;

    /// <summary>online-mode（决定要不要按离线算法生成 UUID）。</summary>
    [ObservableProperty]
    private bool _onlineMode = true;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>服务端是否在运行（决定改动是走命令热生效还是提示重启）。</summary>
    [ObservableProperty]
    private bool _serverRunning;

    /// <summary>控制通道是否可用（不可用时命令下发不了，只能提示重启）。</summary>
    [ObservableProperty]
    private bool _controlAvailable;

    public PlayerListViewModel(InstanceManager manager, string instance)
    {
        _manager = manager;
        _instance = instance;
        _dir = manager.Dir(instance);
    }

    /// <summary>从磁盘加载全部名单与开关。</summary>
    public void Load()
    {
        try
        {
            var status = _manager.Find(_instance);
            ServerRunning = status?.State.IsLive() ?? false;
            ControlAvailable = ServerRunning && InstanceControl.IsAvailable(_dir);

            var props = File.Exists(Path.Combine(_dir, "server.properties"))
                ? ServerPropertiesFile.Load(Path.Combine(_dir, "server.properties"))
                : null;
            WhitelistEnabled = props?.Get("white-list") is "true";
            EnforceWhitelist = props?.Get("enforce-whitelist") is "true";
            OnlineMode = props?.Get("online-mode") is not "false";

            ReloadRows();
            HasError = false;
            StatusText = BuildHint();
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "读取玩家名单失败: " + e.Message;
        }
    }

    private void ReloadRows()
    {
        Whitelist.Clear();
        foreach (var e in PlayerFiles.ReadWhitelist(_dir))
        {
            Whitelist.Add(new WhitelistRow(e.Name, e.Uuid));
        }

        Ops.Clear();
        foreach (var e in PlayerFiles.ReadOps(_dir))
        {
            Ops.Add(new OpRow(e.Name, e.Uuid, e.Level, e.BypassPlayerLimit));
        }

        BannedPlayers.Clear();
        foreach (var e in PlayerFiles.ReadBannedPlayers(_dir))
        {
            BannedPlayers.Add(new BanRow(e.Name, e.Uuid, e.Expires, e.Source));
        }

        BannedIps.Clear();
        foreach (var e in PlayerFiles.ReadBannedIps(_dir))
        {
            BannedIps.Add(new IpBanRow(e.Ip, e.Expires, e.Source));
        }
    }

    /// <summary>
    /// 顶部提示：把「当前配置下会发生什么」说清楚。
    /// 训练员的痛点是「白名单开着却没地方填」，所以白名单开启但列表为空时必须显式警告。
    ///
    /// 分支顺序有讲究：**白名单状态优先于离线模式说明**。「谁能进服」是安全信息，
    /// 必须先说；离线 UUID 那条只是补充说明，不能把它顶掉（否则白名单关着时
    /// 用户看到的是「离线模式…」而完全不知道自己已经对所有人开放了）。
    /// </summary>
    private string BuildHint()
    {
        var hint = WhitelistEnabled switch
        {
            true when Whitelist.Count == 0
                => "白名单已开启但列表为空：没有人能进服。请添加玩家，或关闭「启用白名单」。",
            true => $"白名单已开启，{Whitelist.Count} 人被允许进入。",
            false => "白名单未开启：任何玩家都能进入。",
        };
        if (!OnlineMode)
        {
            hint += " 当前为离线模式（online-mode=false）：UUID 按玩家名本地算出，玩家改名即失效。";
        }
        return hint;
    }

    // ========== 白名单 ==========

    [RelayCommand]
    private async Task AddToWhitelistAsync(CancellationToken ct)
    {
        var name = NewPlayerName.Trim();
        if (name.Length == 0)
        {
            HasError = true;
            StatusText = "请输入玩家名";
            return;
        }
        if (Whitelist.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            HasError = true;
            StatusText = $"{name} 已在白名单中";
            return;
        }

        // 服务端在跑且有控制通道：直接下发 `whitelist add`，让服务端自己解析 UUID 并更新内存。
        // 这是两种模式下都最稳的路径——正版 UUID 由 Mojang 决定、离线 UUID 由服务端算，
        // 我们本地猜都可能错，而猜错的后果是「文件被接受但玩家永远进不来」。
        if (ServerRunning && ControlAvailable)
        {
            try
            {
                InstanceControl.Exec(_dir, "whitelist add " + name);
                ReloadRows();
                HasError = false;
                StatusText = $"已下发 whitelist add {name}，服务端已即时生效。";
                NewPlayerName = "";
                return;
            }
            catch (CeobeException e)
            {
                HasError = true;
                StatusText = "下发 whitelist add 失败: " + e.Message;
                return;
            }
        }

        // 停机路径：本地解析 UUID 后写文件
        var uuid = await ResolveUuidAsync(name, ct).ConfigureAwait(true);
        if (uuid is null) return; // ResolveUuidAsync 已写好失败提示

        var list = PlayerFiles.ReadWhitelist(_dir);
        list.Add(new PlayerFiles.WhitelistEntry(uuid, name));
        PlayerFiles.WriteWhitelist(_dir, list);

        NewPlayerName = "";
        ReloadRows();
        ReportApplied(PlayerListKind.Whitelist);
    }

    /// <summary>
    /// 解析玩家 UUID：离线模式按名本地算；正版模式查 Mojang。
    /// 查不到返回 null 并写好提示——**绝不返回空串**，空串会被服务端当成一个永不匹配的 id。
    /// </summary>
    private async Task<string?> ResolveUuidAsync(string name, CancellationToken ct)
    {
        if (!OnlineMode) return OfflinePlayerUuid.Compute(name);

        StatusText = $"正在查询 {name} 的正版 UUID…";
        var uuid = await MojangUuid.ResolveAsync(name, ct).ConfigureAwait(true);
        if (uuid is null)
        {
            HasError = true;
            StatusText = $"查不到 {name} 的正版账号 UUID（名字可能不存在、拼写有误或网络/限流）。"
                         + "白名单必须写正确 UUID 才生效，故未写入。可改用服务端 `whitelist add` 命令，或核对名字后重试。";
            return null;
        }
        return uuid;
    }

    /// <summary>批量导入（逗号/换行/空格分隔）——训练员提到的「从 OP 导入」也走这里。</summary>
    [RelayCommand]
    private async Task ImportWhitelistAsync(string raw, CancellationToken ct)
    {
        var names = (raw ?? "")
            .Split([',', '\n', '\r', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            HasError = true;
            StatusText = "没有解析到任何玩家名";
            return;
        }

        var list = PlayerFiles.ReadWhitelist(_dir);
        var existing = list.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var todo = names.Where(n => !existing.Contains(n)).ToList();
        if (todo.Count == 0)
        {
            HasError = false;
            StatusText = $"{names.Count} 个名字都已在白名单中";
            return;
        }

        // 服务端在跑：逐个下发 whitelist add，让服务端解析 UUID（两种模式都稳）
        if (ServerRunning && ControlAvailable)
        {
            var failedCmd = new List<string>();
            foreach (var n in todo)
            {
                try
                {
                    InstanceControl.Exec(_dir, "whitelist add " + n);
                }
                catch (CeobeException)
                {
                    failedCmd.Add(n);
                }
            }
            ReloadRows();
            HasError = failedCmd.Count > 0;
            StatusText = failedCmd.Count == 0
                ? $"已下发 {todo.Count} 条 whitelist add，服务端已即时生效。"
                : $"部分下发失败：{string.Join("、", failedCmd)}";
            return;
        }

        // 停机路径：本地解析 UUID。正版模式逐个查 Mojang，失败的**明确列出**，
        // 绝不写空 UUID（服务端会接受文件但玩家永远匹配不上）。
        var added = 0;
        var unresolved = new List<string>();
        for (var i = 0; i < todo.Count; i++)
        {
            var n = todo[i];
            StatusText = $"正在解析 UUID（{i + 1}/{todo.Count}）：{n}";
            string? uuid;
            if (OnlineMode)
            {
                uuid = await MojangUuid.ResolveAsync(n, ct).ConfigureAwait(true);
                if (uuid is null)
                {
                    unresolved.Add(n);
                    continue;
                }
            }
            else
            {
                uuid = OfflinePlayerUuid.Compute(n);
            }
            list.Add(new PlayerFiles.WhitelistEntry(uuid, n));
            added++;
        }
        if (added > 0) PlayerFiles.WriteWhitelist(_dir, list);
        ReloadRows();

        HasError = unresolved.Count > 0;
        StatusText = unresolved.Count == 0
            ? $"导入 {added} 人（跳过 {names.Count - todo.Count} 个重复）"
            : $"导入 {added} 人；{unresolved.Count} 人查不到正版 UUID 故未写入：{string.Join("、", unresolved)}";
        // preserveError：上面刚按 unresolved 置好的错误态不能被清掉，
        // 否则「有 3 人没导入成功」会被一次成功写入抹平，用户以为全都进名单了
        if (added > 0) ReportApplied(PlayerListKind.Whitelist, preserveError: true);
    }

    /// <summary>把当前 OP 列表整体导入白名单（OP 理应也能进服）。</summary>
    [RelayCommand]
    private async Task ImportFromOpsAsync(CancellationToken ct) =>
        await ImportWhitelistAsync(string.Join(",", Ops.Select(o => o.Name)), ct).ConfigureAwait(true);

    [RelayCommand]
    private void RemoveFromWhitelist(string name)
    {
        var list = PlayerFiles.ReadWhitelist(_dir);
        list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        PlayerFiles.WriteWhitelist(_dir, list);
        ReloadRows();
        ReportApplied(PlayerListKind.Whitelist);
    }

    [RelayCommand]
    private void ToggleWhitelist()
    {
        // 写失败时 WriteProp 已置好 HasError/StatusText，这里必须提前返回，
        // 否则下面的 HasError=false 会把失败静默抹掉：界面上开关看着切过去了，磁盘什么都没变。
        if (!WriteProp("white-list", WhitelistEnabled ? "true" : "false")) return;
        ReloadRows();
        HasError = false;
        StatusText = BuildHint();
        // white-list 是需重启项（PropSchema 里标 Restart），不能谎称热生效
        if (ServerRunning)
        {
            StatusText += " 服务端运行中：该开关需重启后生效。";
        }
    }

    [RelayCommand]
    private void ToggleEnforceWhitelist()
    {
        if (!WriteProp("enforce-whitelist", EnforceWhitelist ? "true" : "false")) return;
        HasError = false;
        StatusText = EnforceWhitelist
            ? "已开启强制白名单：未在名单中的在线玩家会被踢出（重启后生效）。"
            : "已关闭强制白名单（重启后生效）。";
    }

    // ========== OP ==========

    [RelayCommand]
    private async Task AddOpAsync(CancellationToken ct)
    {
        var name = NewPlayerName.Trim();
        if (name.Length == 0)
        {
            HasError = true;
            StatusText = "请输入玩家名";
            return;
        }
        var level = Math.Clamp(NewOpLevel, 1, 4);

        // ops.json 没有 reload 命令：运行中必须靠 `op` 命令，服务端会自己写文件。
        // 停机时才由本 VM 直接写文件（且必须带正确 UUID）。
        if (ServerRunning && ControlAvailable)
        {
            try
            {
                foreach (var c in PlayerFiles.OpCommands(name, level)) InstanceControl.Exec(_dir, c);
                ReloadRows();
                HasError = false;
                StatusText = $"已下发 op {name}（等级 {level}），服务端已即时生效。";
                return;
            }
            catch (CeobeException e)
            {
                HasError = true;
                StatusText = "下发 op 命令失败: " + e.Message;
                return;
            }
        }

        var uuid = await ResolveUuidAsync(name, ct).ConfigureAwait(true);
        if (uuid is null) return; // 已写好失败提示，绝不写空 UUID

        var list = PlayerFiles.ReadOps(_dir);
        list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        list.Add(new PlayerFiles.OpEntry(uuid, name, level, false));
        PlayerFiles.WriteOps(_dir, list);
        NewPlayerName = "";
        ReloadRows();
        ReportApplied(PlayerListKind.Ops);
    }

    [RelayCommand]
    private void RemoveOp(string name)
    {
        if (ServerRunning && ControlAvailable)
        {
            try
            {
                foreach (var c in PlayerFiles.UnopCommands(name)) InstanceControl.Exec(_dir, c);
                ReloadRows();
                HasError = false;
                StatusText = $"已下发 deop {name}，服务端已即时生效。";
                return;
            }
            catch (CeobeException e)
            {
                HasError = true;
                StatusText = "下发 deop 命令失败: " + e.Message;
                return;
            }
        }

        var list = PlayerFiles.ReadOps(_dir);
        list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        PlayerFiles.WriteOps(_dir, list);
        ReloadRows();
        ReportApplied(PlayerListKind.Ops);
    }

    // ========== 封禁 ==========

    [RelayCommand]
    private async Task BanPlayerAsync(CancellationToken ct)
    {
        var name = NewPlayerName.Trim();
        if (name.Length == 0)
        {
            HasError = true;
            StatusText = "请输入要封禁的玩家名";
            return;
        }

        // 运行中优先下发 `ban`：服务端会自己解析 UUID、立即踢出在线玩家并写文件，
        // 三件事一次做完，比本地写文件可靠（本地写不会踢人，且正版 UUID 得自己查）。
        if (ServerRunning && ControlAvailable)
        {
            try
            {
                InstanceControl.Exec(_dir, "ban " + name);
                NewPlayerName = "";
                ReloadRows();
                HasError = false;
                StatusText = $"已下发 ban {name}：在线则立即踢出，并写入 banned-players.json。";
                return;
            }
            catch (CeobeException e)
            {
                HasError = true;
                StatusText = "下发 ban 命令失败: " + e.Message;
                return;
            }
        }

        var uuid = await ResolveUuidAsync(name, ct).ConfigureAwait(true);
        if (uuid is null) return; // 已写好失败提示，绝不写空 UUID

        var list = PlayerFiles.ReadBannedPlayers(_dir);
        list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        list.Add(new PlayerFiles.BanEntry(uuid, name, PlayerFiles.NowStamp(), "Ceobe", "forever"));
        PlayerFiles.WriteBannedPlayers(_dir, list);

        NewPlayerName = "";
        ReloadRows();
        ReportApplied(PlayerListKind.BannedPlayers);
    }

    [RelayCommand]
    private void PardonPlayer(string name)
    {
        if (ServerRunning && ControlAvailable)
        {
            try
            {
                foreach (var c in PlayerFiles.PardonCommands(name)) InstanceControl.Exec(_dir, c);
                ReloadRows();
                HasError = false;
                StatusText = $"已下发 pardon {name}。";
                return;
            }
            catch (CeobeException e)
            {
                HasError = true;
                StatusText = "下发 pardon 失败: " + e.Message;
                return;
            }
        }
        var list = PlayerFiles.ReadBannedPlayers(_dir);
        list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        PlayerFiles.WriteBannedPlayers(_dir, list);
        ReloadRows();
        ReportApplied(PlayerListKind.BannedPlayers);
    }

    [RelayCommand]
    private void BanIp()
    {
        var ip = NewIp.Trim();
        if (ip.Length == 0)
        {
            HasError = true;
            StatusText = "请输入要封禁的 IP";
            return;
        }
        var list = PlayerFiles.ReadBannedIps(_dir);
        list.RemoveAll(e => e.Ip == ip);
        list.Add(new PlayerFiles.IpBanEntry(ip, PlayerFiles.NowStamp(), "Ceobe", "forever"));
        PlayerFiles.WriteBannedIps(_dir, list);
        NewIp = "";
        ReloadRows();
        ReportApplied(PlayerListKind.BannedIps);
    }

    [RelayCommand]
    private void PardonIp(string ip)
    {
        if (ServerRunning && ControlAvailable)
        {
            try
            {
                foreach (var c in PlayerFiles.PardonIpCommands(ip)) InstanceControl.Exec(_dir, c);
                ReloadRows();
                HasError = false;
                StatusText = $"已下发 pardon-ip {ip}。";
                return;
            }
            catch (CeobeException e)
            {
                HasError = true;
                StatusText = "下发 pardon-ip 失败: " + e.Message;
                return;
            }
        }
        var list = PlayerFiles.ReadBannedIps(_dir);
        list.RemoveAll(e => e.Ip == ip);
        PlayerFiles.WriteBannedIps(_dir, list);
        ReloadRows();
        ReportApplied(PlayerListKind.BannedIps);
    }

    // ========== 公共 ==========

    /// <summary>
    /// 写一个 server.properties 键（保留注释与其余字节）。
    /// 返回是否成功——**调用方必须检查**：早期版本让调用方无条件把 HasError 置 false，
    /// 于是「没有 server.properties」这类失败被静默吞掉，界面上开关看着已经切过去了，
    /// 实际什么都没写。
    /// </summary>
    private bool WriteProp(string key, string value)
    {
        var path = Path.Combine(_dir, "server.properties");
        if (!File.Exists(path))
        {
            HasError = true;
            StatusText = "尚无 server.properties（先启动一次服务端）";
            return false;
        }
        try
        {
            var f = ServerPropertiesFile.Load(path);
            f.Set(key, value);
            f.Save();
            return true;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = $"写入 {key} 失败: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// 统一汇报「改动是否已在服务端生效」。这是本页最重要的一条产品语义：
    /// 文件写成功不等于生效，界面绝不能让用户以为已经生效。
    ///
    /// 关键：本方法**追加**生效状态到调用方已写的 <see cref="StatusText"/>，而不是覆盖它。
    /// 早期版本覆盖文案，结果批量导入的「导入 N 人 / 跳过 M 个重复 / 有 K 人解析失败」
    /// 被整段抹掉——用户既不知道导入了几个，也收不到失败警告。
    /// </summary>
    /// <param name="preserveError">
    /// 保留调用方已置的 <see cref="HasError"/>。批量导入用 true（部分名字解析失败要继续标红），
    /// 单次操作用 false：既然文件已写成功，就该清除上一次失败留下的错误态，
    /// 否则用户会因为一次早先的失败而看到界面一直红着。
    /// </param>
    private void ReportApplied(PlayerListKind kind, bool preserveError = false)
    {
        if (!preserveError) HasError = false;
        var prefix = StatusText.Trim();
        // 生效状态描述；HasError 只可能由「下发失败」置 true，绝不在此无条件清除
        string effect;
        var commands = PlayerFiles.ReloadCommands(kind, ServerRunning);

        if (!ServerRunning)
        {
            effect = "已写入文件（服务端未运行，启动后生效）。";
        }
        else if (!ControlAvailable)
        {
            effect = PlayerFiles.RequiresRestart(kind)
                ? "已写入文件，但服务端运行中且无控制通道：需重启实例才生效。"
                : "已写入文件，但无控制通道：需重启实例才生效。";
        }
        else if (commands.Count == 0)
        {
            effect = PlayerFiles.RequiresRestart(kind)
                ? "已写入文件，但该名单不支持热加载：需重启实例才生效。"
                : "已写入文件；该名单无 reload 命令，建议改用对应服务端命令或重启。";
        }
        else
        {
            try
            {
                foreach (var c in commands) InstanceControl.Exec(_dir, c);
                effect = $"已写入并下发 {string.Join(" / ", commands)}，服务端已即时生效。";
            }
            catch (CeobeException e)
            {
                HasError = true;
                effect = "文件已写入，但下发 reload 失败: " + e.Message + "（需重启实例才生效）";
            }
        }

        // 追加而非覆盖：保留调用方的统计/警告文案
        StatusText = prefix.Length == 0 ? effect : prefix + " " + effect;
    }
}

/// <summary>白名单行。</summary>
public sealed record WhitelistRow(string Name, string Uuid);

/// <summary>OP 行。</summary>
public sealed record OpRow(string Name, string Uuid, int Level, bool BypassPlayerLimit);

/// <summary>封禁玩家行。</summary>
public sealed record BanRow(string Name, string Uuid, string Expires, string Source);

/// <summary>封禁 IP 行。</summary>
public sealed record IpBanRow(string Ip, string Expires, string Source);
