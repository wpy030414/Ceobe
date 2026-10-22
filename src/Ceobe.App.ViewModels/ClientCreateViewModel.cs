using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Client;
using Ceobe.Core.Jdk;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 新建客户端实例（设计稿 02）：版本 × 加载器，一次选择到底。
///
/// 与「先建个空的、启动时再补」相比，这里把装配前置到创建时：
/// 用户点「创建并下载」之后能看到阶段进度，失败也能就地重试；
/// 而启动页的第一次「启动游戏」应该是秒开的，不该承担 400 MB 的下载。
/// </summary>
public partial class ClientCreateViewModel : ObservableObject
{
    public ObservableCollection<ClientVersionRow> Versions { get; } = [];
    public ObservableCollection<ClientLoaderOption> Loaders { get; } = [];
    public ObservableCollection<string> LoaderVersions { get; } = [];

    [ObservableProperty]
    private ClientVersionRow? _selectedVersion;

    [ObservableProperty]
    private ClientLoaderOption? _selectedLoader;

    [ObservableProperty]
    private string? _selectedLoaderVersion;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _installPath = "";

    [ObservableProperty]
    private string _summaryDownload = "";

    [ObservableProperty]
    private string _summaryJava = "";

    [ObservableProperty]
    private string _summaryServers = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private string _versionHint = "";

    /// <summary>创建成功后的实例名（对话框据此关闭并选中）。</summary>
    public string? Created { get; private set; }

    partial void OnSelectedVersionChanged(ClientVersionRow? value)
    {
        UpdateNameAndSummary();
        _ = ReloadLoaderVersionsAsync();
        OnPropertyChanged(nameof(CanCreate));
    }

    partial void OnSelectedLoaderChanged(ClientLoaderOption? value)
    {
        // 单选语义：手工同步其余选项的选中态（避免 XAML 里用 RadioButton 组再绑一遍）
        foreach (var o in Loaders) o.IsSelected = ReferenceEquals(o, value);
        UpdateNameAndSummary();
        _ = ReloadLoaderVersionsAsync();
        OnPropertyChanged(nameof(CanCreate));
    }

    partial void OnNameChanged(string value)
    {
        InstallPath = string.IsNullOrWhiteSpace(value) ? "" : ClientStore.Dir(value.Trim());
        OnPropertyChanged(nameof(CanCreate));
    }

    partial void OnSelectedLoaderVersionChanged(string? value)
    {
        UpdateNameAndSummary();
        OnPropertyChanged(nameof(CanCreate));
    }

    public bool CanCreate => !IsBusy
        && SelectedVersion is not null
        && SelectedLoader is not null
        && (SelectedLoader.Loader == ClientLoader.VANILLA || !string.IsNullOrEmpty(SelectedLoaderVersion))
        && !string.IsNullOrWhiteSpace(Name);

    /// <summary>首次打开对话框：建加载器选项 + 拉版本清单。</summary>
    public async Task LoadAsync()
    {
        if (Loaders.Count == 0)
        {
            foreach (var loader in new[]
                     {
                         ClientLoader.FABRIC, ClientLoader.NEOFORGE, ClientLoader.FORGE, ClientLoader.VANILLA,
                     })
            {
                var (ok, reason) = ClientVersionCatalog.Support(loader);
                Loaders.Add(new ClientLoaderOption(loader, DescOf(loader), ok, reason));
            }
        }

        var settings = ClientSettings.Load();
        SelectedLoader = Loaders.FirstOrDefault(l => l.Loader == settings.LastLoader) ?? Loaders[0];

        if (Versions.Count > 0) return;

        VersionHint = "正在获取版本清单…";
        try
        {
            var releases = await ClientVersionCatalog.ReleasesAsync().ConfigureAwait(true);
            foreach (var v in releases)
            {
                if (Versions.Count >= 40) break;   // 下拉里没必要塞 900 个版本
                Versions.Add(new ClientVersionRow(v.Id, v.Released,
                    File.Exists(ClientVersion.VersionJson(v.Id))));
            }

            var wanted = settings.LastGameVersion;
            SelectedVersion = Versions.FirstOrDefault(v => v.Id == wanted)
                              ?? Versions.FirstOrDefault(v => v.Id == "1.20.1")
                              ?? Versions.FirstOrDefault(v => v.IsInstalled)
                              ?? Versions.FirstOrDefault();
            VersionHint = Versions.Count == 0 ? "没有取到版本清单" : "";
        }
        catch (CeobeException e)
        {
            VersionHint = "无法获取版本清单：" + e.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        Versions.Clear();
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task ReloadLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        var loader = SelectedLoader?.Loader ?? ClientLoader.VANILLA;
        var mc = SelectedVersion?.Id;
        if (mc is null) return;
        if (loader == ClientLoader.VANILLA)
        {
            UpdateNameAndSummary();
            return;
        }
        if (!ClientVersionCatalog.Support(loader).Ok)
        {
            UpdateNameAndSummary();
            return;
        }

        try
        {
            var list = await ClientVersionCatalog.LoaderVersionsAsync(loader, mc).ConfigureAwait(true);
            foreach (var v in list) LoaderVersions.Add(v);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
        }
        catch (CeobeException e)
        {
            HasError = true;
            ErrorText = $"无法获取 {loader.Label()} 的版本列表：{e.Message}";
        }
        UpdateNameAndSummary();
    }

    private void UpdateNameAndSummary()
    {
        var mc = SelectedVersion?.Id;
        var loader = SelectedLoader?.Loader ?? ClientLoader.VANILLA;

        if (string.IsNullOrWhiteSpace(Name) && mc is not null)
        {
            Name = loader == ClientLoader.VANILLA ? mc : $"{mc} {loader.Label()}";
        }

        if (mc is null)
        {
            SummaryDownload = "选择版本后显示将下载的内容";
            SummaryJava = "";
            SummaryServers = "";
            return;
        }

        var loaderText = loader == ClientLoader.VANILLA
            ? "原版（无加载器）"
            : $"{loader.Label()} {SelectedLoaderVersion}";
        var installed = File.Exists(ClientVersion.VersionJson(mc));
        SummaryDownload = installed
            ? $"{mc} 已在缓存中 · 只补加载器与缺失文件"
            : $"{mc} · {loaderText} · 约 300–500 MB（客户端 + 库 + 资源）";

        int java;
        try
        {
            java = ClientVersion.ReadJavaMajorFromCache(mc);
        }
        catch (Exception)
        {
            java = 21;
        }
        var hasJava = JdkStore.FindMajor(java) is not null
                      || (JavaEnv.HostJavaHome() is { } host && JavaEnv.MajorOf(host) == java);
        SummaryJava = hasJava
            ? $"复用已安装的 Java {java}"
            : $"需要 Java {java}（未检测到，可在设置里一键安装）";

        var servers = ClientServerLinks.Collect(new ClientMeta { Name = "__probe__", GameVersion = mc });
        SummaryServers = servers.Count == 0
            ? "没有可写入的服务端（可稍后在「服务器」分区添加）"
            : $"servers.dat 写入 {servers.Count} 台 Ceobe 服务端";
    }

    /// <summary>
    /// 创建 + 装配。两步分开：目录与元数据先落盘（失败可重来），
    /// 下载失败时实例仍然存在——用户可以进实例点「修复游戏文件」，而不是从零再来一遍。
    /// </summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate || SelectedVersion is null || SelectedLoader is null) return;
        IsBusy = true;
        HasError = false;
        ProgressText = "正在创建实例…";
        ProgressValue = 0;
        OnPropertyChanged(nameof(CanCreate));

        var meta = new ClientMeta
        {
            Name = Name.Trim(),
            GameVersion = SelectedVersion.Id,
            Loader = SelectedLoader.Loader,
            LoaderVersion = SelectedLoader.Loader == ClientLoader.VANILLA ? null : SelectedLoaderVersion,
            PlayerName = ClientSettings.Load().ActiveOrDefault().Name,
        };

        try
        {
            ClientStore.Create(meta);

            // 预写 servers.dat：新建完就能在多人列表里看到 ceobe 的服务端
            try
            {
                var entries = ClientServerLinks.Collect(meta);
                if (entries.Count > 0) ServersDatFile.Sync(ClientStore.ServersDat(meta.Name), entries);
            }
            catch (Exception)
            {
                // 预写失败不影响创建，启动时还会再 Sync 一次
            }

            var sink = new ProgressSink(this);
            await Task.Run(() => ClientInstaller.InstallAsync(meta, sink)).ConfigureAwait(true);

            var settings = ClientSettings.Load();
            settings.LastGameVersion = meta.GameVersion;
            settings.LastLoader = meta.Loader;
            settings.Save();

            Created = meta.Name;
            ProgressText = "创建完成";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            ErrorText = e.Message;
            ProgressText = "创建中断：" + e.Message.Split('\n')[0];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HasError = true;
            ErrorText = e.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    private static string DescOf(ClientLoader loader) => loader switch
    {
        ClientLoader.FABRIC => "轻量 · 更新最快",
        ClientLoader.NEOFORGE => "1.20.2+ 主线",
        ClientLoader.FORGE => "老版本兼容",
        _ => "不装加载器",
    };

    private sealed class ProgressSink(ClientCreateViewModel owner) : IProgressSink
    {
        public void Update(string stage, long done, long total) => UiThread.Post(() =>
        {
            owner.ProgressText = stage;
            owner.ProgressValue = total > 0 ? Math.Clamp(done / (double)total * 100, 0, 100) : 0;
        });
    }
}

/// <summary>版本列表一行。</summary>
public sealed record ClientVersionRow(string Id, DateTimeOffset? Released, bool IsInstalled)
{
    public string TypeLabel => IsInstalled ? "正式版 · 已安装" : "正式版";

    public string ReleasedLabel => Released is { } r ? r.ToLocalTime().ToString("yyyy-MM-dd") : "";
}

/// <summary>加载器选项（含「暂不支持」的明确说明）。</summary>
public partial class ClientLoaderOption : ObservableObject
{
    public ClientLoaderOption(ClientLoader loader, string description, bool supported, string reason)
    {
        Loader = loader;
        Description = supported ? description : reason;
        Supported = supported;
    }

    public ClientLoader Loader { get; }
    public string Label => Loader.Label();
    public string Description { get; }
    public bool Supported { get; }
    public bool IsSelectable => Supported;

    [ObservableProperty]
    private bool _isSelected;
}
