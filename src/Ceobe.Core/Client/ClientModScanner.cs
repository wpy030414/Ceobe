using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>一条前置依赖声明。</summary>
public sealed record ModDependency(string Id, string? VersionRange);

/// <summary>mods 目录里的一项。</summary>
public sealed record ModEntry(
    string FileName,
    string? ModId,
    string? DisplayName,
    string? Version,
    bool Enabled,
    long SizeBytes,
    IReadOnlyList<ModDependency> Depends,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Satisfied)
{
    /// <summary>「依赖健康」列文案——设计稿要求它是列级信息，而不是藏在详情里。</summary>
    public string Health
    {
        get
        {
            if (Missing.Count > 0) return "缺少前置 " + string.Join("、", Missing);
            if (Satisfied.Count > 0) return "需要 " + string.Join("、", Satisfied) + "（已满足）";
            return "前置齐全";
        }
    }
}

/// <summary>
/// mods 目录扫描与依赖健康判定。
///
/// 依赖信息就躺在 jar 里的 <c>fabric.mod.json</c> / <c>META-INF/mods.toml</c>——
/// 读它不需要联网，也不会误判。设计稿把「依赖健康」做成列，正是因为
/// 「少一个前置 → 游戏黑屏」是启动器用户最常见也最难自查的失败模式。
///
/// 停用 = 把 <c>x.jar</c> 改名成 <c>x.jar.disabled</c>：Fabric/Forge 只加载 .jar，
/// 这样既能一键还原，也不会在文件系统上丢失任何信息（比「移到 disabled 子目录」更简单，
/// 用户自己在资源管理器里也能看懂）。
/// </summary>
public static partial class ClientModScanner
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>平台自身不是「用户装的 Mod」，不该出现在依赖清单里。</summary>
    private static readonly HashSet<string> PlatformIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric", "quilt_loader", "forge", "neoforge",
    };

    [GeneratedRegex(@"^\s*modId\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex TomlModIdRegex();

    [GeneratedRegex(@"^\s*displayName\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex TomlDisplayNameRegex();

    [GeneratedRegex(@"^\s*version\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex TomlVersionRegex();

    [GeneratedRegex(@"modId\s*=\s*""([^""]+)""")]
    private static partial Regex TomlDepIdRegex();

    public static IReadOnlyList<ModEntry> Scan(string clientName)
    {
        var dir = ClientStore.ModsDir(clientName);
        var jars = new List<(string Path, bool Enabled, string FileName)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    jars.Add((file, true, name));
                }
                else if (name.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    jars.Add((file, false, name[..^DisabledSuffix.Length]));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var parsed = jars
            .Select(j => (j.FileName, j.Enabled, Info: ReadInfo(j.Path)))
            .ToList();

        // 依赖是否满足只看「已启用」的 Mod——停用的 Mod 等于没装，这是用户最容易误解的一点
        var present = parsed
            .Where(p => p.Enabled && p.Info.Id is not null)
            .Select(p => p.Info.Id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<ModEntry>();
        foreach (var (fileName, enabled, info) in parsed)
        {
            var missing = new List<string>();
            var satisfied = new List<string>();
            foreach (var dep in info.Depends)
            {
                if (PlatformIds.Contains(dep.Id)) continue;
                if (present.Contains(dep.Id)) satisfied.Add(dep.Id);
                else missing.Add(dep.Id);
            }

            long size = 0;
            try
            {
                var path = Path.Combine(dir, enabled ? fileName : fileName + DisabledSuffix);
                size = new FileInfo(path).Length;
            }
            catch (Exception) { }

            result.Add(new ModEntry(fileName, info.Id, info.Name, info.Version, enabled, size,
                info.Depends, missing, satisfied));
        }

        return result
            .OrderBy(m => m.Missing.Count > 0 ? 0 : 1)
            .ThenBy(m => m.DisplayName ?? m.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>启用/停用：改名而非移动，见类注释。</summary>
    public static void SetEnabled(string clientName, string fileName, bool enabled)
    {
        var dir = ClientStore.ModsDir(clientName);
        var active = Path.Combine(dir, fileName);
        var disabled = active + DisabledSuffix;

        try
        {
            if (enabled && File.Exists(disabled))
            {
                if (File.Exists(active)) throw CeobeException.Of("同名 jar 已存在：{0}", fileName);
                File.Move(disabled, active);
            }
            else if (!enabled && File.Exists(active))
            {
                if (File.Exists(disabled)) File.Delete(disabled);
                File.Move(active, disabled);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"切换 {fileName} 状态失败：{e.Message}", e);
        }
    }

    public static void Remove(string clientName, string fileName, bool enabled)
    {
        var path = Path.Combine(ClientStore.ModsDir(clientName),
            enabled ? fileName : fileName + DisabledSuffix);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"删除 {fileName} 失败：{e.Message}", e);
        }
    }

    /// <summary>导入一个 jar（拖放 / 文件对话框）。同名覆盖，返回最终文件名。</summary>
    public static string Import(string clientName, string sourceJar)
    {
        if (!File.Exists(sourceJar)) throw CeobeException.Of("文件不存在：{0}", sourceJar);
        if (!sourceJar.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new CeobeException("只接受 .jar（整合包请到「整合包」分区导入）");

        var fileName = Path.GetFileName(sourceJar);
        var target = Path.Combine(ClientStore.ModsDir(clientName), fileName);
        try
        {
            File.Copy(sourceJar, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"导入 {fileName} 失败：{e.Message}", e);
        }
        return fileName;
    }

    /// <summary>
    /// 从 jar 里读元数据。先按 Fabric（fabric.mod.json）、再按 Forge/NeoForge（mods.toml）。
    /// 两个都没有时不报错——很多老 Mod 只有 mcmod.info，读不到就退回文件名，界面照样能用。
    /// </summary>
    private static (string? Id, string? Name, string? Version, List<ModDependency> Depends) ReadInfo(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var fabric = zip.GetEntry("fabric.mod.json");
            if (fabric is not null)
            {
                using var reader = new StreamReader(fabric.Open());
                return ParseFabric(reader.ReadToEnd());
            }

            var toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (toml is not null)
            {
                using var reader = new StreamReader(toml.Open());
                return ParseToml(reader.ReadToEnd());
            }
        }
        catch (Exception)
        {
            // 损坏的 jar / 非法 zip：当作「读不到元数据」，不要让整张表崩掉
        }
        return (null, null, null, []);
    }

    internal static (string? Id, string? Name, string? Version, List<ModDependency> Depends) ParseFabric(string json)
    {
        using var doc = JsonUtil.Parse(json);
        var root = doc.RootElement;
        var id = root.Prop("id").AsText();
        var name = root.Prop("name").AsText();
        var version = root.Prop("version").AsText();

        var depends = new List<ModDependency>();
        var dep = root.Prop("depends");
        foreach (var p in dep.Properties())
        {
            var range = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            depends.Add(new ModDependency(p.Name, range));
        }
        return (id, name, version, depends);
    }

    internal static (string? Id, string? Name, string? Version, List<ModDependency> Depends) ParseToml(string toml)
    {
        string? First(Regex re)
        {
            var m = re.Match(toml);
            return m.Success ? m.Groups[1].Value : null;
        }

        // 依赖段落形如：
        //   [[dependencies.create]]
        //   modId="flywheel"
        //   mandatory=true
        // 正则取每个 dependencies 段落里的 modId，已足够用于「缺前置」判定。
        var depends = new List<ModDependency>();
        var sections = Regex.Split(toml, @"^\s*\[\[", RegexOptions.Multiline);
        foreach (var section in sections)
        {
            if (!section.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase)) continue;
            var idMatch = TomlDepIdRegex().Match(section);
            if (idMatch.Success)
            {
                depends.Add(new ModDependency(idMatch.Groups[1].Value, null));
            }
        }

        return (First(TomlModIdRegex()), First(TomlDisplayNameRegex()), First(TomlVersionRegex()), depends);
    }
}
