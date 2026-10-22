using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>资源包/数据包的一项。</summary>
public sealed record PackEntry(
    string FileName,
    string DisplayName,
    bool Enabled,
    long SizeBytes,
    int? PackFormat);

/// <summary>光影包的一项。</summary>
public sealed record ShaderEntry(string FileName, string DisplayName, bool Active, long SizeBytes);

/// <summary>
/// 资源包 / 数据包 / 光影包的目录与开关。
///
/// 「启用状态」不在文件系统上，而在 <c>options.txt</c> 的 <c>resourcePacks</c> 数组里，
/// 且**数组顺序即优先级**——越靠前覆盖越优先。所以这个模块必须做三件事：
/// 列目录、读写 options.txt 的那一行、以及调整顺序。任何一步偷懒都会让用户的材质包「莫名失效」。
/// </summary>
public static class PackCatalog
{
    private const string PacksKey = "resourcePacks";

    public static IReadOnlyList<PackEntry> List(string clientName)
    {
        var dir = ClientStore.ResourcePacksDir(clientName);
        var options = OptionsTxt.Load(ClientStore.OptionsFile(clientName));
        var enabled = options.GetArray(PacksKey)
            .Where(v => v.StartsWith("file/", StringComparison.OrdinalIgnoreCase))
            .Select(v => v["file/".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<PackEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                var isDir = Directory.Exists(path);
                if (!isDir && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                var (display, format) = ReadMeta(path, isDir);
                result.Add(new PackEntry(
                    name,
                    display ?? name,
                    enabled.Contains(name),
                    isDir ? SaveCatalog.DirectorySize(path) : SafeLength(path),
                    format));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 目录读不到就返回已发现部分
        }

        // 已启用的排前面并按 options 顺序，其余按名字——用户看到的顺序要和游戏里的优先级一致
        var order = options.GetArray(PacksKey)
            .Select(v => v.StartsWith("file/", StringComparison.OrdinalIgnoreCase) ? v["file/".Length..] : v)
            .ToList();
        return result
            .OrderBy(p => p.Enabled ? IndexOf(order, p.FileName) : int.MaxValue)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>启用/停用：直接改 options.txt 的数组，不移动文件（停用 ≠ 卸载）。</summary>
    public static void SetEnabled(string clientName, string fileName, bool enabled)
    {
        var file = ClientStore.OptionsFile(clientName);
        var options = OptionsTxt.Load(file);
        var current = options.GetArray(PacksKey).ToList();
        var id = "file/" + fileName;

        var exists = current.Any(v => string.Equals(v, id, StringComparison.OrdinalIgnoreCase));
        if (enabled && !exists)
        {
            // 追加到末尾 = 优先级最低，符合「新加的包不覆盖已有材质」的直觉
            current.Add(id);
        }
        else if (!enabled && exists)
        {
            current.RemoveAll(v => string.Equals(v, id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            return;
        }
        options.SetArray(PacksKey, current);
        options.Save();
    }

    /// <summary>在启用列表里上下移动（<paramref name="delta"/> = -1 上移/优先级更高，+1 下移）。</summary>
    public static void Move(string clientName, string fileName, int delta)
    {
        var file = ClientStore.OptionsFile(clientName);
        var options = OptionsTxt.Load(file);
        var current = options.GetArray(PacksKey).ToList();
        var id = "file/" + fileName;
        var at = current.FindIndex(v => string.Equals(v, id, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;

        var target = Math.Clamp(at + delta, 0, current.Count - 1);
        if (target == at) return;
        var item = current[at];
        current.RemoveAt(at);
        current.Insert(target, item);
        options.SetArray(PacksKey, current);
        options.Save();
    }

    // —— 光影：Iris 用 config/iris.properties 的 shaderPack=，没有「顺序」，只有「当前哪个」 ——

    public static IReadOnlyList<ShaderEntry> ListShaders(string clientName)
    {
        var dir = ClientStore.ShaderPacksDir(clientName);
        var active = ActiveShader(clientName);
        var result = new List<ShaderEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                var isDir = Directory.Exists(path);
                if (!isDir && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new ShaderEntry(
                    name,
                    name,
                    string.Equals(active, name, StringComparison.OrdinalIgnoreCase),
                    isDir ? SaveCatalog.DirectorySize(path) : SafeLength(path)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return result
            .OrderBy(s => s.Active ? 0 : 1)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string? ActiveShader(string clientName)
    {
        var props = Path.Combine(ClientStore.Dir(clientName), "config", "iris.properties");
        return ReadKey(props, "shaderPack");
    }

    /// <summary>设为当前光影；传 null 表示关闭光影（保留 enableShaders 键以兼容 Iris 的行为）。</summary>
    public static void SetActiveShader(string clientName, string? fileName)
    {
        var props = Path.Combine(ClientStore.Dir(clientName), "config", "iris.properties");
        WriteKey(props, "shaderPack", fileName ?? "");
        WriteKey(props, "enableShaders", fileName is null ? "false" : "true");
    }

    // —— helpers ——

    private static int IndexOf(List<string> order, string name)
    {
        var i = order.FindIndex(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? int.MaxValue : i;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }

    /// <summary>读 pack.mcmeta 里的显示名与 pack_format；失败返回 (null, null)。</summary>
    internal static (string? Name, int? Format) ReadMeta(string path, bool isDir)
    {
        try
        {
            string? json = null;
            if (isDir)
            {
                var meta = Path.Combine(path, "pack.mcmeta");
                if (File.Exists(meta)) json = File.ReadAllText(meta);
            }
            else
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("pack.mcmeta");
                if (entry is not null)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    json = reader.ReadToEnd();
                }
            }
            if (json is null) return (null, null);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("pack", out var pack)) return (null, null);
            int? format = pack.TryGetProperty("pack_format", out var pf) && pf.TryGetInt32(out var n) ? n : null;
            string? name = pack.TryGetProperty("description", out var desc)
                ? DescriptionToText(desc)
                : null;
            return (name, format);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>description 可能是字符串，也可能是 {"text": "..."} 文本组件。</summary>
    private static string? DescriptionToText(JsonElement desc)
    {
        switch (desc.ValueKind)
        {
            case JsonValueKind.String:
                return desc.GetString();
            case JsonValueKind.Object when desc.TryGetProperty("text", out var t):
                return t.GetString();
            default:
                return null;
        }
    }

    private static string? ReadKey(string path, string key)
    {
        if (!File.Exists(path)) return null;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                if (string.Equals(line[..i].Trim(), key, StringComparison.Ordinal))
                {
                    return line[(i + 1)..].Trim();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>保序改写 key=value（iris.properties 由游戏自己维护，不能整文件重写）。</summary>
    private static void WriteKey(string path, string key, string value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(lines[i][..eq].Trim(), key, StringComparison.Ordinal))
            {
                lines[i] = key + "=" + value;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(key + "=" + value);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }
}
