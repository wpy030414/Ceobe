using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「服务器配置」页 ViewModel（Java 版 <c>ServerPane</c>）：实例级设置，落 instance.json，
/// **不写 server.properties**（那是「游戏配置」页的事，两页共用同一实例目录，
/// 各改各的文件，互不覆盖）。
///
/// 关键语义（都从 Java 版逐条搬来，且都有产品后果）：
/// - 运行中禁止修改：内存/端口改动要重启才生效，运行中改了会让用户误以为已生效；
/// - 改名走 <see cref="InstanceManager.Rename"/>（同步改目录 + 元数据），与其余设置分两步；
/// - 改端口会由 Core 同步写 server.properties 的 server-port/query.port（穿透地址依赖它）；
/// - 设 authlib-injector 会由 Core 自动关闭 online-mode（三方鉴权需要，ADR-015）。
/// </summary>
public partial class ServerViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly string _instance;

    /// <summary>原始实例名（改名时对比用）。</summary>
    public string OriginalName { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private int _memoryMb = 2048;

    [ObservableProperty]
    private int _port = 25565;

    [ObservableProperty]
    private string _jvmArgs = "";

    [ObservableProperty]
    private string? _authlibInjectorUrl;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>实例是否在运行（运行中禁止修改设置）。</summary>
    [ObservableProperty]
    private bool _serverRunning;

    /// <summary>保存成功后改名，需通知上层重建页签（页签标题是实例名）。</summary>
    public event Action<string>? Renamed;

    public ServerViewModel(InstanceManager manager, string instance)
    {
        _manager = manager;
        _instance = instance;
        OriginalName = instance;
    }

    /// <summary>内存滑杆范围（对齐 Java 版 Spinner 512..65536）。</summary>
    public int MemoryMin => 512;
    public int MemoryMax => 65536;

    /// <summary>是否可保存：非运行中且未在处理中。</summary>
    public bool CanSave => !ServerRunning && !IsBusy;

    /// <summary>运行中提示（对齐 Java 版：实例运行中；停止后方可修改）。</summary>
    public string RunningHint => ServerRunning ? "实例运行中；停止后方可修改内存/端口等设置。" : "";

    partial void OnServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(RunningHint));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    /// <summary>从 meta 加载当前设置。</summary>
    public void Load()
    {
        var meta = _manager.Meta(_instance);
        Name = meta.Name;
        MemoryMb = meta.MemoryMb;
        Port = meta.Port;
        JvmArgs = meta.JvmArgs ?? "";
        AuthlibInjectorUrl = meta.AuthlibInjectorUrl;
        ServerRunning = _manager.Find(_instance)?.State.IsLive() ?? false;
        HasError = false;
        StatusText = RunningHint;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (ServerRunning)
        {
            HasError = true;
            StatusText = "实例正在运行，请先停止再修改设置";
            return;
        }

        IsBusy = true;
        var target = _instance;
        try
        {
            var newName = Name.Trim();
            // ① 改名先行（Core 要求已停止；同步改目录与元数据）
            if (!string.IsNullOrEmpty(newName) && newName != _instance)
            {
                await Task.Run(() => _manager.Rename(_instance, newName), ct).ConfigureAwait(true);
                target = newName;
            }

            // ② 其余设置：端口为 0 表示不改（Core 的 UpdateInstance 用 null 表示不改动）
            await Task.Run(() => _manager.UpdateInstance(
                target,
                MemoryMb,
                Port,               // 端口总是传：Core 内部会判断是否变化并同步 properties
                JvmArgs,
                AuthlibInjectorUrl), ct).ConfigureAwait(true);

            HasError = false;
            // 内存/端口改动需重启才生效，如实告知（对齐 Java 版）
            StatusText = "已保存服务器配置（内存/端口等重启后生效）";
            if (target != _instance)
            {
                Renamed?.Invoke(target);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "保存失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>放弃改动，重新从磁盘加载。</summary>
    [RelayCommand]
    private void Discard() => Load();

    /// <summary>当前值是否与磁盘不同（决定「保存」按钮是否高亮）。</summary>
    public bool IsDirty
    {
        get
        {
            var meta = _manager.Find(_instance)?.Meta;
            if (meta is null) return false;
            return Name.Trim() != meta.Name
                   || MemoryMb != meta.MemoryMb
                   || Port != meta.Port
                   || (JvmArgs ?? "") != (meta.JvmArgs ?? "")
                   || (AuthlibInjectorUrl ?? "") != (meta.AuthlibInjectorUrl ?? "");
        }
    }
}
