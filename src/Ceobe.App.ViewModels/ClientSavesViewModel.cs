using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ceobe.Core.Client;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「存档」分区（设计稿预留项，本轮补齐）。核心是**备份**：
/// 存档是玩家唯一不可再生的资产，而启动器最容易做的蠢事就是「一键清理」。
/// 所以这里只有「备份 / 还原」，删除世界不在本页提供。
/// </summary>
public partial class ClientSavesViewModel : ObservableObject
{
    private readonly ClientMeta _meta;
    private readonly ClientRuntime _runtime;

    public ObservableCollection<ClientSaveCard> Saves { get; } = [];

    [ObservableProperty]
    private string _hint = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasError;

    public ClientSavesViewModel(ClientRuntime runtime, ClientMeta meta)
    {
        _runtime = runtime;
        _meta = meta;
    }

    /// <summary>实例名（页面「打开存档目录」用）。</summary>
    public string InstanceName => _meta.Name;

    /// <summary>是否展示状态条（StatusMessage 非空）。</summary>
    public bool HasStatus => StatusMessage.Length > 0;

    public bool ShowError => HasStatus && HasError;

    public bool ShowSuccess => HasStatus && !HasError;

    public void Reload()
    {
        Saves.Clear();
        var list = SaveCatalog.List(_meta.Name);
        foreach (var s in list) Saves.Add(new ClientSaveCard(s));
        Hint = list.Count == 0
            ? "还没有世界。进游戏创建世界后，这里会列出每个世界的名称、模式与最后游玩时间。"
            : $"{list.Count} 个世界 · 按最后游玩时间排序";
    }

    /// <summary>备份为 zip。要求游戏已退出（region 文件在运行中是半写的）。</summary>
    public void Backup(ClientSaveCard? row)
    {
        if (row is null) return;
        if (_runtime.IsRunning(_meta.Name))
        {
            HasError = true;
            StatusMessage = "游戏正在运行，先退出游戏再备份（运行中的世界文件是半写的，备份出来不可用）";
            return;
        }
        try
        {
            var path = SaveCatalog.Backup(_meta.Name, row.Folder);
            HasError = false;
            StatusMessage = "已备份到 " + path;
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(ShowSuccess));
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusMessage = "备份失败：" + e.Message;
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(ShowSuccess));
        }
    }

    public void Restore(string zipPath)
    {
        try
        {
            var dir = SaveCatalog.Restore(_meta.Name, zipPath);
            HasError = false;
            StatusMessage = "已还原到 " + dir;
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(ShowSuccess));
            Reload();
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusMessage = "还原失败：" + e.Message;
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(ShowSuccess));
        }
    }
}

public sealed class ClientSaveCard
{
    public ClientSaveCard(SaveEntry entry)
    {
        Folder = entry.Folder;
        LevelName = entry.LevelName;
        GameMode = entry.GameMode;
        SizeLabel = TextFormat.HumanSize(entry.SizeBytes);
        VersionLabel = entry.VersionName is { } v ? $"存档版本 {v}" : "";
        LastPlayedLabel = entry.LastPlayed is { } t
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "未知时间";
        HasIcon = entry.IconPath is not null;
    }

    public string Folder { get; }
    public string LevelName { get; }
    public string GameMode { get; }
    public string SizeLabel { get; }
    public string VersionLabel { get; }
    public string LastPlayedLabel { get; }
    public bool HasIcon { get; }

    public string MetaLine => string.Join(" · ", new[] { GameMode, SizeLabel, VersionLabel }
        .Where(s => !string.IsNullOrEmpty(s)));
}
