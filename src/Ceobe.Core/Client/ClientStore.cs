using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// 客户端实例仓库：<c>&lt;home&gt;/clients/&lt;name&gt;/</c>，与实例目录（<c>instances/</c>）平级。
///
/// 目录即实例，无中央索引（沿用服务端 ADR-005 的取舍）：删掉目录 = 删掉实例，
/// 拷到别的机器改个 folder 名就能用。游戏文件的子目录布局刻意与 <c>.minecraft</c> 一致
/// （mods / resourcepacks / saves / logs / options.txt / servers.dat），
/// 这样用户从任何启动器迁过来都能直接把整个目录拖进去。
/// </summary>
public static class ClientStore
{
    /// <summary>实例名长度上限（与服务端一致，便于界面按同样宽度排布）。</summary>
    private const int MaxNameLength = 32;

    public static string Root() => CeobeHome.Ensure(Path.Combine(CeobeHome.Root(), "clients"));

    public static string Dir(string name) => Path.Combine(Root(), name);

    // —— 共享缓存：libraries / assets / 版本 JSON 不按实例复制 ——
    //
    // 一次 1.20.1 的 libraries + assets 约 400 MB，每个实例存一份会让「多实例切换」
    // 这个核心用法付出磁盘代价。--assetsDir 与完整路径的 classpath 都允许共享，
    // 所以这两块放 cache 下，实例只独占 natives、mods、存档与日志。

    public static string CacheRoot() => CeobeHome.Ensure(Path.Combine(CeobeHome.Root(), "cache", "client"));

    public static string LibrariesDir() => CeobeHome.Ensure(Path.Combine(CacheRoot(), "libraries"));

    public static string AssetsDir() => CeobeHome.Ensure(Path.Combine(CacheRoot(), "assets"));

    public static string VersionsDir() => CeobeHome.Ensure(Path.Combine(CacheRoot(), "versions"));

    // —— 实例内布局 ——

    public static string ModsDir(string name) => CeobeHome.Ensure(Path.Combine(Dir(name), "mods"));

    public static string ResourcePacksDir(string name) =>
        CeobeHome.Ensure(Path.Combine(Dir(name), "resourcepacks"));

    public static string ShaderPacksDir(string name) => CeobeHome.Ensure(Path.Combine(Dir(name), "shaderpacks"));

    public static string SavesDir(string name) => CeobeHome.Ensure(Path.Combine(Dir(name), "saves"));

    public static string LogsDir(string name) => CeobeHome.Ensure(Path.Combine(Dir(name), "logs"));

    public static string CrashReportsDir(string name) => CeobeHome.Ensure(Path.Combine(Dir(name), "crash-reports"));

    public static string NativesDir(string name) => CeobeHome.Ensure(Path.Combine(Dir(name), "natives"));

    public static string OptionsFile(string name) => Path.Combine(Dir(name), "options.txt");

    public static string ServersDat(string name) => Path.Combine(Dir(name), "servers.dat");

    /// <summary>本次会话的日志文件（每次启动覆盖，与 .minecraft/logs/latest.log 同语义）。</summary>
    public static string LatestLog(string name) => Path.Combine(LogsDir(name), "latest.log");

    // —— CRUD ——

    public static List<ClientMeta> List()
    {
        var result = new List<ClientMeta>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root()))
            {
                var metaFile = Path.Combine(dir, "client.json");
                if (!File.Exists(metaFile)) continue;
                try
                {
                    result.Add(ReadMeta(dir));
                }
                catch (CeobeException)
                {
                    // 单个实例元数据损坏不该让整张列表失效（对齐服务端 List 的取舍）
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public static ClientMeta? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var dir = Dir(name);
        return File.Exists(Path.Combine(dir, "client.json")) ? ReadMeta(dir) : null;
    }

    public static ClientMeta Require(string name) =>
        Find(name) ?? throw CeobeException.Of("客户端实例不存在: {0}", name);

    /// <summary>
    /// 客户端实例名允许中文与空格旁的可打印字符——设计稿里的实例就叫「生存重启」。
    /// 服务端那边限制成 ascii 小写是因为名字会进 server.properties 与命令行；
    /// 客户端的名字只用于目录与界面，没必要付同样的可读性代价。
    /// 仍然拒绝路径分隔符、保留名与控制字符，否则会写出到实例目录之外。
    /// </summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CeobeException("实例名不能为空");
        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
            throw CeobeException.Of("实例名最多 {0} 个字符", MaxNameLength);
        if (trimmed is "." or "..")
            throw new CeobeException("实例名不能是 . 或 ..");
        if (trimmed != name)
            throw new CeobeException("实例名首尾不能有空白");
        foreach (var c in trimmed)
        {
            if (char.IsControl(c)) throw new CeobeException("实例名不能包含控制字符");
        }
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CeobeException("实例名不能包含 \\ / : * ? \" < > |");
        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
            throw new CeobeException("实例名不能以点或空格结尾");
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "LPT1", "LPT2" };
        if (reserved.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            throw CeobeException.Of("{0} 是 Windows 保留名，换一个", trimmed);
    }

    /// <summary>
    /// 落盘元数据并建好标准子目录。已存在同名实例时抛异常（不做静默覆盖）。
    /// </summary>
    public static void Create(ClientMeta meta)
    {
        ValidateName(meta.Name);
        var dir = Dir(meta.Name);
        if (File.Exists(Path.Combine(dir, "client.json")))
            throw CeobeException.Of("客户端实例已存在: {0}", meta.Name);

        CeobeHome.Ensure(dir);
        // 子目录先建好：新建完就能往里拖 mod / 存档，不必等第一次启动
        _ = ModsDir(meta.Name);
        _ = ResourcePacksDir(meta.Name);
        _ = ShaderPacksDir(meta.Name);
        _ = SavesDir(meta.Name);
        _ = LogsDir(meta.Name);
        _ = CrashReportsDir(meta.Name);
        Save(meta);
    }

    public static void Save(ClientMeta meta)
    {
        var dir = Dir(meta.Name);
        CeobeHome.Ensure(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "client.json"), JsonUtil.Write(meta),
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"写入客户端元数据失败: {dir}", e);
        }
    }

    public static void Rename(string oldName, string newName)
    {
        if (oldName == newName) return;
        ValidateName(newName);
        var from = Dir(oldName);
        var to = Dir(newName);
        if (!File.Exists(Path.Combine(from, "client.json")))
            throw CeobeException.Of("客户端实例不存在: {0}", oldName);
        if (File.Exists(Path.Combine(to, "client.json")))
            throw CeobeException.Of("客户端实例已存在: {0}", newName);
        try
        {
            Directory.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"重命名失败: {e.Message}", e);
        }
        var meta = ReadMeta(to);
        meta.Name = newName;
        Save(meta);
    }

    /// <summary>删除实例。<paramref name="purge"/>=false 只删元数据，保留存档与 mods。</summary>
    public static void Remove(string name, bool purge)
    {
        var dir = Dir(name);
        if (!File.Exists(Path.Combine(dir, "client.json")))
            throw CeobeException.Of("客户端实例不存在: {0}", name);
        try
        {
            if (purge)
            {
                ZipUtil.DeleteRecursively(dir);
            }
            else
            {
                File.Delete(Path.Combine(dir, "client.json"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"删除客户端实例失败: {e.Message}", e);
        }
    }

    internal static ClientMeta ReadMeta(string dir)
    {
        try
        {
            return JsonUtil.Parse<ClientMeta>(
                File.ReadAllText(Path.Combine(dir, "client.json"), new System.Text.UTF8Encoding(false)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"读取客户端元数据失败: {dir}", e);
        }
    }
}
