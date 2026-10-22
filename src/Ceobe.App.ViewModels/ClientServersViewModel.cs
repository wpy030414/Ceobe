using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Ceobe.Core.Client;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「服务器」分区（设计稿 04）。这一页是客户端编排相对通用启动器的结构性差异：
/// Ceobe 服务端实例的穿透地址自动出现在这里，点「加入」直接带 <c>--server</c> 起游戏，
/// 版本不匹配时按钮降级为提示，而不是让用户撞「版本握手失败」。
/// </summary>
public partial class ClientServersViewModel : ObservableObject
{
    private readonly ClientMeta _meta;

    public ObservableCollection<ClientServerRow> Servers { get; } = [];

    [ObservableProperty]
    private bool _isPinging;

    [ObservableProperty]
    private string _statusLine = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>请求启动并直连某地址。由 ClientTabs 接到启动页的 LaunchCommand。</summary>
    public event Action<string>? JoinRequested;

    public ClientServersViewModel(ClientMeta meta) => _meta = meta;

    public void Reload()
    {
        Servers.Clear();

        var ceobe = ClientServerLinks.LoadServers();
        foreach (var s in ceobe)
        {
            var address = ClientServerLinks.AddressOf(s);
            var viaTunnel = s.LastPinggyAddress is not null || s.LastCloudflareUrl is not null;
            Servers.Add(new ClientServerRow
            {
                Name = s.Name,
                Address = address ?? "",
                Kind = "Ceobe 服务端",
                Source = viaTunnel ? "穿透通道" : "本机回环",
                IsCeobe = true,
                IsPaired = string.Equals(s.Name, _meta.PairedServer, StringComparison.Ordinal),
                VersionHint = s.GameVersion,
                VersionMatches = string.Equals(s.GameVersion, _meta.GameVersion, StringComparison.Ordinal),
            });
        }

        // servers.dat 里手动加的服务器（去重：已由 Ceobe 覆盖的 ip 不再列一遍）
        var known = Servers.Select(s => s.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ServersDatFile.Load(ClientStore.ServersDat(_meta.Name)))
        {
            if (known.Contains(m.Ip)) continue;
            Servers.Add(new ClientServerRow
            {
                Name = m.Name,
                Address = m.Ip,
                Kind = "手动添加",
                Source = "servers.dat",
                IsCeobe = false,
                VersionMatches = true,
            });
        }

        StatusLine = Servers.Count == 0
            ? "没有可加入的服务器"
            : $"{Servers.Count} 台 · {Servers.Count(s => s.IsPaired)} 台已配对";
    }

    /// <summary>
    /// 刷新全部延迟/在线数。并发探测，单个失败只影响它自己。
    /// 5 秒超时，因为这是「点一下看看」的动作，不该让用户等 30 秒。
    /// </summary>
    public async Task PingAllAsync(CancellationToken ct = default)
    {
        if (IsPinging || Servers.Count == 0) return;
        IsPinging = true;
        StatusLine = "正在探测…";
        try
        {
            var tasks = Servers.Select(async row =>
            {
                var (host, port) = SplitAddress(row.Address);
                if (host is null) return;
                var sw = Stopwatch.StartNew();
                var result = await McPing.ProbeAsync(host, port, TimeSpan.FromSeconds(5), ct)
                    .ConfigureAwait(false);
                sw.Stop();
                var ms = (int)sw.ElapsedMilliseconds;
                UiThread.Post(() =>
                {
                    // 延迟只在探测成功时有意义；失败显示超时，避免「0 ms」被误读成极快
                    row.Latency = result.Verified ? $"{ms} ms" : "超时";
                    ApplyProbe(row, result);
                });
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(true);
            var online = Servers.Count(s => s.Online);
            StatusLine = $"{Servers.Count} 台 · {online} 台可达";
        }
        finally
        {
            IsPinging = false;
        }
    }

    private static void ApplyProbe(ClientServerRow row, McPingResult result)
    {
        if (!result.Verified || result.Json is null)
        {
            row.Online = false;
            row.Players = "无法获取";
            row.StatusKey = "off";
            row.StatusLabel = "无法验证";
            return;
        }

        try
        {
            using var doc = JsonUtil.Parse(result.Json);
            var root = doc.RootElement;
            var players = root.GetProperty("players");
            var online = players.GetProperty("online").GetInt32();
            var max = players.GetProperty("max").GetInt32();
            row.Players = $"{online}/{max} 在线";
            row.VersionName = root.GetProperty("version").GetProperty("name").GetString() ?? "";
            row.Online = true;
            row.StatusKey = "ok";
            row.StatusLabel = "可达";
            if (!string.Equals(row.VersionHint, row.VersionName, StringComparison.Ordinal)
                && row.VersionName.Length > 0)
            {
                row.VersionMatches = row.VersionName.Contains(row.VersionHint, StringComparison.Ordinal);
            }
        }
        catch (Exception)
        {
            row.Online = false;
            row.StatusKey = "warn";
            row.StatusLabel = "响应无法解析";
        }
    }

    public void Join(ClientServerRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Address))
        {
            HasError = true;
            return;
        }
        if (!row.VersionMatches)
        {
            HasError = true;
            return;
        }
        JoinRequested?.Invoke(row.Address);
    }

    /// <summary>把 Ceobe 服务端重新写进 servers.dat（用户手动删过条目时用）。</summary>
    public void SyncServersDat()
    {
        try
        {
            var entries = ClientServerLinks.Collect(_meta);
            var (added, updated) = ServersDatFile.Sync(ClientStore.ServersDat(_meta.Name), entries);
            StatusLine = added + updated == 0
                ? "servers.dat 已是最新"
                : $"已写入 servers.dat：新增 {added}，更新 {updated}";
            Reload();
        }
        catch (Exception e) when (e is CeobeException or IOException or UnauthorizedAccessException)
        {
            HasError = true;
            StatusLine = "写入 servers.dat 失败：" + e.Message;
        }
    }

    private static (string? Host, int Port) SplitAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (null, 0);
        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and <= 65535)
        {
            return (address[..colon], port);
        }
        return (address, 25565);
    }
}

/// <summary>服务器卡片。探测结果会就地更新（不重建集合，避免列表跳动）。</summary>
public partial class ClientServerRow : ObservableObject
{
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsCeobe { get; init; }
    public bool IsPaired { get; init; }
    public string VersionHint { get; init; } = "";
    public bool VersionMatches { get; set; } = true;

    [ObservableProperty]
    private bool _online;

    [ObservableProperty]
    private string _players = "—";

    [ObservableProperty]
    private string _latency = "—";

    [ObservableProperty]
    private string _statusKey = "idle";

    [ObservableProperty]
    private string _statusLabel = "未探测";

    [ObservableProperty]
    private string _versionName = "";

    public bool CanJoin => VersionMatches && !string.IsNullOrWhiteSpace(Address);

    public string JoinLabel => VersionMatches ? "加入服务器" : "版本不匹配";

    public string MetaLine => string.Join(" · ", new[] { Source, Players, Latency }
        .Where(s => !string.IsNullOrEmpty(s)));

    public string WarningLine => VersionMatches
        ? ""
        : $"客户端是 {VersionHint} 之外的版本，直接加入会被拒绝";
}
