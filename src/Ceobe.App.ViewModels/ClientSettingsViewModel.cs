using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Client;
using Ceobe.Core.Jdk;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「设置」分区（设计稿 08）。全局层与实例层在这里**同时**可见，因为用户经常分不清
/// 「这个参数是只对这个实例生效，还是全都生效」——卡片标题直接写清楚。
///
/// 账户与 Java 是全局的（挂在 <c>client-settings.json</c>）；内存、JVM 参数、玩家名是实例的
/// （写回该实例的 <c>client.json</c>）。
/// </summary>
public partial class ClientSettingsViewModel : ObservableObject
{
    private readonly ClientMeta _meta;

    public ObservableCollection<ClientAccountRow> Accounts { get; } = [];
    public ObservableCollection<ClientJavaRow> Javas { get; } = [];
    public ObservableCollection<string> ServerChoices { get; } = [];

    [ObservableProperty]
    private string _newAccountName = "";

    [ObservableProperty]
    private int _memoryMb;

    /// <summary>NumberBox 绑定用（控件是 double，模型是 int）。</summary>
    public double MemoryValue
    {
        get => MemoryMb;
        set
        {
            var v = (int)Math.Round(value);
            if (v != MemoryMb) MemoryMb = v;
        }
    }

    partial void OnMemoryMbChanged(int value) => OnPropertyChanged(nameof(MemoryValue));

    [ObservableProperty]
    private string _jvmArgs = "";

    [ObservableProperty]
    private string _gameArgs = "";

    [ObservableProperty]
    private string _playerName = "";

    [ObservableProperty]
    private string _selectedServer = NotPaired;

    [ObservableProperty]
    private string _globalJvmArgs = "";

    [ObservableProperty]
    private string _javaHome = "";

    [ObservableProperty]
    private string _mirrorText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasError;

    private const string NotPaired = "（不配对）";

    public ClientSettingsViewModel(ClientMeta meta) => _meta = meta;

    public void Reload()
    {
        var settings = ClientSettings.Load();

        Accounts.Clear();
        foreach (var a in settings.Accounts)
        {
            Accounts.Add(new ClientAccountRow(a, a.Name == settings.ActiveAccount));
        }
        if (Accounts.Count == 0)
        {
            // 没配置过账户时给一个默认离线身份，避免「账户」卡片空着让人以为坏了
            var fallback = settings.ActiveOrDefault();
            Accounts.Add(new ClientAccountRow(fallback, true));
        }

        Javas.Clear();
        foreach (var jdk in JdkStore.List())
        {
            Javas.Add(new ClientJavaRow($"Java {jdk.Major} · {Path.GetFileName(jdk.Home)}",
                "ceobe 自管", RangeOf(jdk.Major), jdk.Home, "已校验"));
        }
        if (JavaEnv.HostJavaHome() is { } host)
        {
            var major = JavaEnv.MajorOf(host) ?? -1;
            Javas.Add(new ClientJavaRow(
                major > 0 ? $"Java {major} · 系统安装" : "系统安装的 Java",
                "JAVA_HOME", major > 0 ? RangeOf(major) : "未知", host, major > 0 ? "可用" : "未校验"));
        }
        if (Javas.Count == 0)
        {
            Javas.Add(new ClientJavaRow("未检测到 Java", "—", "—", "", "缺失"));
        }

        MemoryMb = _meta.MemoryMb;
        JvmArgs = _meta.JvmArgs;
        GameArgs = _meta.GameArgs;
        PlayerName = _meta.PlayerName;
        GlobalJvmArgs = settings.JvmArgs;
        JavaHome = _meta.JavaHome ?? settings.JavaHome ?? "";

        ServerChoices.Clear();
        ServerChoices.Add(NotPaired);
        foreach (var s in ClientServerLinks.LoadServers()) ServerChoices.Add(s.Name);
        SelectedServer = string.IsNullOrWhiteSpace(_meta.PairedServer) ? NotPaired : _meta.PairedServer;

        MirrorText = Mirrors.IsEnabled
            ? "游戏文件 / 库 → BMCLAPI（2 秒不回退直连） · Modrinth → mcimirror · Temurin → 清华"
            : "镜像已关闭（CEOBE_MIRROR=off），全部直连官方源";
    }

    /// <summary>参考区间：权威来源永远是版本 JSON 的 javaVersion，这里只帮用户回忆。</summary>
    private static string RangeOf(int major) => major switch
    {
        <= 8 => "≤ 1.17.1",
        11 or 16 => "极少使用",
        17 => "1.18 – 1.20.4",
        21 => "1.20.5 – 1.21.x",
        >= 25 => "26.x+",
        _ => "按版本 JSON 匹配",
    };

    [RelayCommand]
    private void AddOffline()
    {
        var name = NewAccountName.Trim();
        if (name.Length == 0)
        {
            HasError = true;
            StatusMessage = "离线账户名不能为空";
            return;
        }
        try
        {
            var settings = ClientSettings.Load();
            var account = new ClientAccount { Name = name, Kind = AccountKind.OFFLINE };
            settings.Upsert(account);
            settings.ActiveAccount = name;
            settings.Save();
            NewAccountName = "";
            HasError = false;
            StatusMessage = $"已添加离线账户 {name}（UUID 由名字派生，服务端需 online-mode=false）";
            Reload();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HasError = true;
            StatusMessage = "保存账户失败：" + e.Message;
        }
    }

    public void UseAccount(ClientAccountRow? row)
    {
        if (row is null) return;
        try
        {
            var settings = ClientSettings.Load();
            settings.Upsert(row.Account);
            settings.ActiveAccount = row.Account.Name;
            settings.Save();
            HasError = false;
            StatusMessage = $"已切换到 {row.Account.Name}";
            Reload();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HasError = true;
            StatusMessage = "切换账户失败：" + e.Message;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var meta = ClientStore.Find(_meta.Name) ?? _meta;
            meta.MemoryMb = Math.Clamp(MemoryMb, 512, 32768);
            meta.JvmArgs = JvmArgs.Trim();
            meta.GameArgs = GameArgs.Trim();
            meta.PlayerName = PlayerName.Trim();
            meta.JavaHome = string.IsNullOrWhiteSpace(JavaHome) ? null : JavaHome.Trim();
            meta.PairedServer = SelectedServer == NotPaired ? null : SelectedServer;
            ClientStore.Save(meta);

            var settings = ClientSettings.Load();
            settings.JvmArgs = GlobalJvmArgs.Trim();
            settings.Save();

            HasError = false;
            StatusMessage = "已保存（下次启动生效）";
        }
        catch (Exception e) when (e is CeobeException or IOException or UnauthorizedAccessException)
        {
            HasError = true;
            StatusMessage = "保存失败：" + e.Message;
        }
    }
}

public sealed class ClientAccountRow
{
    public ClientAccountRow(ClientAccount account, bool active)
    {
        Account = account;
        IsActive = active;
    }

    public ClientAccount Account { get; }
    public bool IsActive { get; }
    public string Name => Account.Name;
    public string Sub => Account.KindLabel;
    public string Badge => IsActive ? "使用中" : "";
}

public sealed class ClientJavaRow
{
    public ClientJavaRow(string title, string source, string range, string home, string status)
    {
        Title = title;
        Source = source;
        Range = range;
        Home = home;
        Status = status;
    }

    public string Title { get; }
    public string Source { get; }
    public string Range { get; }
    public string Home { get; }
    public string Status { get; }
    public bool IsOk => Status is "已校验" or "可用";
}
