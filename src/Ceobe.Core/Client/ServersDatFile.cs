using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>servers.dat 里的一条服务器记录。</summary>
public sealed record ServerEntry(string Name, string Ip);

/// <summary>
/// <c>servers.dat</c>（未压缩的 NBT：根 compound 的 <c>servers</c> 列表，每项含 name / ip / icon）。
///
/// 启动器要做的关键动作是「把服务端实例的穿透地址写进多人列表」——
/// 没有它，用户就得从服务端页复制地址、进游戏、点添加服务器、粘贴、保存，
/// 而这正是 ceobe 相对通用启动器唯一的差异化体验（设计稿 04 分区）。
///
/// 合并策略：按 <c>ip</c> 认领。已存在的记录只更新名字，保留 icon / acceptTextures
/// 等我们不认识的字段——启动器不该是「每次启动都重置用户服务器列表」的元凶。
/// </summary>
public static class ServersDatFile
{
    private const string ListKey = "servers";

    public static IReadOnlyList<ServerEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var root = NbtFile.Read(path);
            var list = root[ListKey];
            if (list is null) return [];
            var result = new List<ServerEntry>();
            foreach (var item in list.Items)
            {
                var name = item["name"]?.AsString();
                var ip = item["ip"]?.AsString();
                if (string.IsNullOrWhiteSpace(ip)) continue;
                result.Add(new ServerEntry(string.IsNullOrWhiteSpace(name) ? ip : name, ip));
            }
            return result;
        }
        catch (Exception)
        {
            // 文件损坏不该阻止启动：当作空列表处理，下次 Sync 会重建
            return [];
        }
    }

    public static void Save(string path, IEnumerable<ServerEntry> servers)
    {
        var root = NbtTag.Compound();
        if (File.Exists(path))
        {
            try { root = NbtFile.Read(path); }
            catch (Exception) { root = NbtTag.Compound(); }
        }
        var list = NbtTag.List(NbtType.Compound);
        foreach (var s in servers)
        {
            list.Add(NbtTag.Compound()
                .Set("name", NbtTag.String(s.Name))
                .Set("ip", NbtTag.String(s.Ip)));
        }
        root.Set(ListKey, list);
        NbtFile.Write(path, root, gzip: false);
    }

    /// <summary>
    /// 把 <paramref name="desired"/> 合并进现有文件（按 ip 去重），返回新增/更新条数。
    /// 文件不存在或损坏时自动重建。
    /// </summary>
    public static (int Added, int Updated) Sync(string path, IReadOnlyList<ServerEntry> desired)
    {
        NbtTag root;
        try
        {
            root = File.Exists(path) ? NbtFile.Read(path) : NbtTag.Compound();
        }
        catch (Exception)
        {
            root = NbtTag.Compound();
        }

        var existing = root[ListKey];
        if (existing is null || existing.Type != NbtType.List)
        {
            existing = NbtTag.List(NbtType.Compound);
            root.Set(ListKey, existing);
        }

        var index = new Dictionary<string, NbtTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing.Items)
        {
            var ip = item["ip"]?.AsString();
            if (!string.IsNullOrWhiteSpace(ip)) index[ip!] = item;
        }

        var added = 0;
        var updated = 0;
        foreach (var want in desired)
        {
            if (index.TryGetValue(want.Ip, out var tag))
            {
                var currentName = tag["name"]?.AsString();
                if (!string.Equals(currentName, want.Name, StringComparison.Ordinal))
                {
                    tag.Set("name", NbtTag.String(want.Name));
                    updated++;
                }
                continue;
            }
            var fresh = NbtTag.Compound()
                .Set("name", NbtTag.String(want.Name))
                .Set("ip", NbtTag.String(want.Ip))
                .Set("acceptTextures", NbtTag.Byte(1));
            existing.Add(fresh);
            index[want.Ip] = fresh;
            added++;
        }

        if (added > 0 || updated > 0 || !File.Exists(path))
        {
            NbtFile.Write(path, root, gzip: false);
        }
        return (added, updated);
    }

    /// <summary>
    /// 从服务端实例构造记录：优先用穿透公网地址（Pinggy / Cloudflare），
    /// 其次用局域网直连地址（<c>host:port</c>）。两者都没有时不写——空地址会变成一条点不进去的死条目。
    /// </summary>
    public static List<ServerEntry> FromCeobeServers(
        IEnumerable<(string Name, int Port, string? Pinggy, string? Cloudflare)> servers,
        string? hostFallback = null)
    {
        var list = new List<ServerEntry>();
        foreach (var s in servers)
        {
            var address = NormalizeAddress(s.Pinggy) ?? NormalizeAddress(s.Cloudflare);
            address ??= $"{hostFallback ?? "127.0.0.1"}:{s.Port}";
            list.Add(new ServerEntry(s.Name, address));
        }
        return list;
    }

    /// <summary>
    /// 穿透地址可能只有主机名（Pinggy 常见 <c>xxx.pinggy.io</c>），而 servers.dat 需要 <c>host:port</c>。
    /// 缺端口时补 25565；已经是 host:port 的原样返回。
    /// </summary>
    internal static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        // 有些通道会返回带协议的 URL，servers.dat 只认 host:port
        if (a.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(a, UriKind.Absolute, out var uri))
        {
            a = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        if (a.StartsWith('[')) return a;                        // IPv6 字面量，不猜端口
        var colon = a.LastIndexOf(':');
        if (colon > 0 && int.TryParse(a[(colon + 1)..], out var port) && port is > 0 and <= 65535) return a;
        return a + ":25565";
    }
}
