using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ceobe.Core.Client;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「资源包」分区（设计稿 07）。三条规则：拖放即导入、状态胶囊即开关、顺序即优先级。
///
/// 没有「保存」按钮：开关与排序都是**即时写回 options.txt** 的。启动器不该要求用户
/// 记得再点一次保存——忘点之后进游戏发现材质没生效，是这个页面最典型的抱怨。
/// </summary>
public partial class ClientPacksViewModel : ObservableObject
{
    private readonly ClientMeta _meta;

    public ObservableCollection<ClientPackRow> Packs { get; } = [];
    public ObservableCollection<ClientShaderRow> Shaders { get; } = [];

    [ObservableProperty]
    private ClientPackRow? _selectedPack;

    [ObservableProperty]
    private string _packNote = "";

    [ObservableProperty]
    private string _shaderNote = "";

    [ObservableProperty]
    private string _error = "";

    public ClientPacksViewModel(ClientMeta meta) => _meta = meta;

    /// <summary>实例名（页面「打开文件夹」需要拼路径）。</summary>
    public string DirectoryName => _meta.Name;

    public void Reload()
    {
        Packs.Clear();
        var list = PackCatalog.List(_meta.Name);
        foreach (var p in list) Packs.Add(new ClientPackRow(p));
        PackNote = list.Count == 0
            ? "还没有资源包。把 .zip 拖到此页即可导入。"
            : $"{list.Count(p => p.Enabled)} / {list.Count} 已启用 · 越靠上优先级越高";

        Shaders.Clear();
        var shaders = PackCatalog.ListShaders(_meta.Name);
        foreach (var s in shaders) Shaders.Add(new ClientShaderRow(s));
        var hasIris = File.Exists(Path.Combine(ClientStore.Dir(_meta.Name), "config", "iris.properties"))
                      || ClientModScanner.Scan(_meta.Name).Any(m => m.ModId is "iris" or "oculus");
        ShaderNote = shaders.Count == 0
            ? "还没有光影包。需要先装 Iris（Fabric）或 Oculus（Forge）。"
            : hasIris
                ? $"{shaders.Count} 套 · 通过 Iris 加载"
                : $"{shaders.Count} 套 · 未检测到 Iris，启用后也不会生效";
    }

    public string? Toggle(ClientPackRow? row)
    {
        if (row is null) return null;
        return SetError(null, () =>
        {
            PackCatalog.SetEnabled(_meta.Name, row.FileName, !row.Enabled);
            Reload();
        });
    }

    /// <summary>上移/下移。只在已启用项之间有意义，故未启用时直接忽略。</summary>
    public void Move(ClientPackRow? row, int delta)
    {
        if (row is null || !row.Enabled) return;
        try
        {
            PackCatalog.Move(_meta.Name, row.FileName, delta);
            Reload();
            Error = "";
        }
        catch (CeobeException e)
        {
            Error = e.Message;
        }
    }

    public void ToggleShader(ClientShaderRow? row)
    {
        if (row is null) return;
        try
        {
            // 光影是单选：点已启用的那个 = 关闭光影
            PackCatalog.SetActiveShader(_meta.Name, row.Active ? null : row.FileName);
            Reload();
            Error = "";
        }
        catch (Exception e) when (e is CeobeException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
    }

    /// <summary>导入资源包（拖放 / 手填路径）。</summary>
    public string? ImportPack(string path)
    {
        return CopyInto(path, ClientStore.ResourcePacksDir(_meta.Name));
    }

    public string? ImportShader(string path)
    {
        return CopyInto(path, ClientStore.ShaderPacksDir(_meta.Name));
    }

    private string? CopyInto(string path, string destDir)
    {
        try
        {
            if (!File.Exists(path)) throw CeobeException.Of("文件不存在：{0}", path);
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new CeobeException("只接受 .zip 格式的资源包 / 光影包");
            }
            File.Copy(path, Path.Combine(destDir, name), overwrite: true);
            Reload();
            return null;
        }
        catch (Exception e) when (e is CeobeException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private string? SetError(string? message, Action action)
    {
        try
        {
            action();
            Error = "";
            return null;
        }
        catch (Exception e) when (e is CeobeException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            return e.Message;
        }
    }
}

public sealed class ClientPackRow
{
    public ClientPackRow(PackEntry entry)
    {
        FileName = entry.FileName;
        DisplayName = entry.DisplayName;
        Enabled = entry.Enabled;
        SizeLabel = TextFormat.HumanSize(entry.SizeBytes);
        FormatLabel = entry.PackFormat is { } f ? $"pack_format {f}" : "";
    }

    public string FileName { get; }
    public string DisplayName { get; }
    public bool Enabled { get; }
    public string SizeLabel { get; }
    public string FormatLabel { get; }
    public string StateLabel => Enabled ? "已启用" : "已停用";
}

public sealed class ClientShaderRow
{
    public ClientShaderRow(ShaderEntry entry)
    {
        FileName = entry.FileName;
        DisplayName = entry.DisplayName;
        Active = entry.Active;
        SizeLabel = TextFormat.HumanSize(entry.SizeBytes);
    }

    public string FileName { get; }
    public string DisplayName { get; }
    public bool Active { get; }
    public string SizeLabel { get; }
    public string StateLabel => Active ? "已启用" : "未启用";
}
