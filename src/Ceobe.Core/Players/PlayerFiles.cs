using System.Globalization;
using System.Text.Json.Nodes;
using Ceobe.Core.Util;

namespace Ceobe.Core.Players;

/// <summary>名单种类。</summary>
public enum PlayerListKind
{
    Whitelist,
    Ops,
    BannedPlayers,
    BannedIps,
}

/// <summary>
/// 玩家名单文件读写：whitelist.json / ops.json / banned-players.json / banned-ips.json。
///
/// 为什么这层是净新增（Java 版完全没有）：训练员反馈「白名单默认开启，但没有入口填写白名单，
/// 只能在控制台输入指令」。全仓检索确认原版零处处理这些文件，故这里从零定义。
///
/// 三条决定本层 API 形状的事实（都是「静默失败」的来源）：
/// 1. 服务端**在内存里缓存名单**。运行中直接改文件不一定生效：
///    - whitelist：有 <c>whitelist reload</c> 可热加载；
///    - ops / 封禁表：**没有对应的 reload 命令**，只能靠 <c>op/deop</c>、<c>pardon/pardon-ip</c>
///      这类命令让服务端自己改内存并顺手写文件，或重启。
///    所以本层把「写文件」与「该下发什么命令」分开暴露（<see cref="ReloadCommands"/>），
///    由 GUI/CLI 组成原子操作，两侧不各猜一半。
/// 2. **写出一律是裸 JSON 数组**（实测原版 26.3 的唯一接受形态）。早期版本写成
///    <c>{"version":N,"profileList":[…]}</c>，真实服务端读它直接抛 Gson 解析异常，
///    白名单等于没生效——见下方 <see cref="WriteArray"/> 的说明。读入则保留宽容：
///    裸数组与旧 envelope 都认，让被旧版本写坏的文件仍能在界面上显示、并在下次写入时自动修复。
/// 3. 离线模式（online-mode=false）下 whitelist 要写 UUID，而 UUID 由玩家名按
///    <see cref="OfflinePlayerUuid"/> 算出——算错的话服务端接受文件但玩家照样进不来。
/// </summary>
public static class PlayerFiles
{
    /// <summary>
    /// MC 写这些文件的时间戳格式。Java 侧是 <c>SimpleDateFormat("yyyy-MM-dd HH:mm:ss Z")</c>，
    /// 实测本机输出 <c>2025-10-09 16:53:20 +0800</c>（偏移无冒号）。
    ///
    /// .NET 没有能输出该形态的说明符：<c>z</c>/<c>zz</c> 给 <c>+00</c>、<c>zzz</c> 给 <c>+00:00</c>，
    /// 都不是 Java <c>Z</c> 的 <c>+0000</c>。故日期部分用模板、偏移量手工拼装（见 <see cref="NowStamp"/>）。
    /// 本实现统一写 UTC（偏移恒 <c>+0000</c>），语义等价且不受时区/夏令时设置影响。
    /// </summary>
    internal const string StampPattern = "yyyy-MM-dd HH:mm:ss";

    // 注：永久封禁在文件里就是字符串 "forever"（原版实测：banned-players.json 的 expires 字段），
    // 没有额外的毫秒数派生字段；早期版本自造的 expiresLong 已移除。

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // —— 记录类型 ——

    /// <summary>白名单条目。</summary>
    public sealed record WhitelistEntry(string Uuid, string Name);

    /// <summary>OP 条目。<paramref name="Level"/> 1..4，语义同 <c>op-permission-level</c>。</summary>
    public sealed record OpEntry(string Uuid, string Name, int Level, bool BypassPlayerLimit);

    /// <summary>封禁玩家条目。<paramref name="Expires"/> 用 MC 文本形式，"forever" 表示永久。</summary>
    public sealed record BanEntry(string Uuid, string Name, string Created, string Source, string Expires);

    /// <summary>封禁 IP 条目。</summary>
    public sealed record IpBanEntry(string Ip, string Created, string Source, string Expires);

    // —— 路径 ——

    public static string WhitelistPath(string instanceDir) => Path.Combine(instanceDir, "whitelist.json");
    public static string OpsPath(string instanceDir) => Path.Combine(instanceDir, "ops.json");
    public static string BannedPlayersPath(string instanceDir) => Path.Combine(instanceDir, "banned-players.json");
    public static string BannedIpsPath(string instanceDir) => Path.Combine(instanceDir, "banned-ips.json");

    // —— 读 ——

    public static List<WhitelistEntry> ReadWhitelist(string instanceDir) =>
        ReadNameUuidPairs(WhitelistPath(instanceDir), "uuid", "name")
            .Select(p => new WhitelistEntry(p.Uuid, p.Name)).ToList();

    public static List<OpEntry> ReadOps(string instanceDir)
    {
        var outList = new List<OpEntry>();
        foreach (var node in ReadArray(OpsPath(instanceDir)))
        {
            var name = Str(node, "name");
            if (string.IsNullOrEmpty(name)) continue;
            outList.Add(new OpEntry(
                UuidOf(node, name!),
                name!,
                ClampLevel(Int(node, "level", 4)),
                Bool(node, "bypassesPlayerLimit", false)));
        }
        return outList;
    }

    public static List<BanEntry> ReadBannedPlayers(string instanceDir)
    {
        var outList = new List<BanEntry>();
        foreach (var node in ReadArray(BannedPlayersPath(instanceDir)))
        {
            var name = Str(node, "name") ?? "";
            outList.Add(new BanEntry(
                UuidOf(node, name),
                name,
                Str(node, "created") ?? "",
                Str(node, "source") ?? "unknown",
                Str(node, "expires") ?? "forever"));
        }
        return outList;
    }

    public static List<IpBanEntry> ReadBannedIps(string instanceDir)
    {
        var outList = new List<IpBanEntry>();
        foreach (var node in ReadArray(BannedIpsPath(instanceDir)))
        {
            var ip = Str(node, "ip");
            if (string.IsNullOrEmpty(ip)) continue;
            outList.Add(new IpBanEntry(
                ip!,
                Str(node, "created") ?? "",
                Str(node, "source") ?? "unknown",
                Str(node, "expires") ?? "forever"));
        }
        return outList;
    }

    // —— 写 ——

    public static void WriteWhitelist(string instanceDir, IReadOnlyList<WhitelistEntry> entries)
    {
        var list = new JsonArray();
        foreach (var e in Dedup(entries, x => x.Name))
        {
            list.Add(new JsonObject { ["uuid"] = e.Uuid, ["name"] = e.Name });
        }
        WriteArray(WhitelistPath(instanceDir), list);
    }

    public static void WriteOps(string instanceDir, IReadOnlyList<OpEntry> entries)
    {
        var list = new JsonArray();
        foreach (var e in Dedup(entries, x => x.Name))
        {
            list.Add(new JsonObject
            {
                ["uuid"] = e.Uuid,
                ["name"] = e.Name,
                ["level"] = ClampLevel(e.Level),
                ["bypassesPlayerLimit"] = e.BypassPlayerLimit,
            });
        }
        WriteArray(OpsPath(instanceDir), list);
    }

    public static void WriteBannedPlayers(string instanceDir, IReadOnlyList<BanEntry> entries)
    {
        var list = new JsonArray();
        foreach (var e in Dedup(entries, x => x.Name))
        {
            var created = string.IsNullOrEmpty(e.Created) ? NowStamp() : e.Created;
            var expires = string.IsNullOrEmpty(e.Expires) ? "forever" : e.Expires;
            list.Add(new JsonObject
            {
                ["uuid"] = e.Uuid,
                ["name"] = e.Name,
                ["created"] = created,
                ["source"] = string.IsNullOrEmpty(e.Source) ? "Ceobe" : e.Source,
                ["expires"] = expires,
            });
        }
        WriteArray(BannedPlayersPath(instanceDir), list);
    }

    public static void WriteBannedIps(string instanceDir, IReadOnlyList<IpBanEntry> entries)
    {
        var list = new JsonArray();
        foreach (var e in Dedup(entries, x => x.Ip))
        {
            var created = string.IsNullOrEmpty(e.Created) ? NowStamp() : e.Created;
            var expires = string.IsNullOrEmpty(e.Expires) ? "forever" : e.Expires;
            list.Add(new JsonObject
            {
                ["ip"] = e.Ip,
                ["created"] = created,
                ["source"] = string.IsNullOrEmpty(e.Source) ? "Ceobe" : e.Source,
                ["expires"] = expires,
            });
        }
        WriteArray(BannedIpsPath(instanceDir), list);
    }

    // —— 命令下发 ——

    /// <summary>
    /// 写完文件后，让改动在服务端**内存里**生效所需下发的控制台命令。
    /// 只有 whitelist 有 reload；ops 与封禁表返回空集，调用方必须改走
    /// <see cref="OpCommands"/>/<see cref="UnopCommands"/> 或提示重启——
    /// 把这条差异写在一处，界面就不会谎称「已生效」。
    /// </summary>
    public static IReadOnlyList<string> ReloadCommands(PlayerListKind kind, bool serverRunning)
    {
        if (!serverRunning) return [];
        return kind switch
        {
            // enforce-whitelist=true 时 reload 会踢掉未授权在线玩家，这是 MC 自身行为
            PlayerListKind.Whitelist => ["whitelist reload"],
            PlayerListKind.Ops => [],
            PlayerListKind.BannedPlayers => [],
            PlayerListKind.BannedIps => [],
            _ => [],
        };
    }

    /// <summary>该名单种类改完后是否必须重启才生效（供界面提示措辞，别让用户白改）。</summary>
    public static bool RequiresRestart(PlayerListKind kind) =>
        kind is PlayerListKind.BannedPlayers or PlayerListKind.BannedIps;

    /// <summary>授权 OP：以命令为准（服务端自己写 ops.json，所以本层不必写文件）。</summary>
    public static IReadOnlyList<string> OpCommands(string name, int level = 4) => [$"op {name}"];

    /// <summary>撤销 OP：命令 + 文件都要动，缺一不可（内存态与磁盘态分离）。</summary>
    public static IReadOnlyList<string> UnopCommands(string name) => [$"deop {name}"];

    /// <summary>解封玩家（运行期唯一可靠手段，服务端会同步写文件）。</summary>
    public static IReadOnlyList<string> PardonCommands(string name) => [$"pardon {name}"];

    /// <summary>解封 IP。</summary>
    public static IReadOnlyList<string> PardonIpCommands(string ip) => [$"pardon-ip {ip}"];

    // —— 辅助 ——

    private static string UuidOf(JsonNode? node, string name) =>
        OfflinePlayerUuid.Normalize(Str(node, "uuid")) ?? OfflinePlayerUuid.Compute(name);

    private static int ClampLevel(int level) => Math.Clamp(level, 1, 4);

    /// <summary>
    /// 读 JSON 数组：兼容「裸数组」与「envelope（profileList/ops/items）」两种形态；
    /// 文件不存在或解析失败一律返回空集——损坏的名单文件不该挡住开服。
    /// </summary>
    internal static List<JsonNode?> ReadArray(string path)
    {
        if (!File.Exists(path)) return [];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path, new System.Text.UTF8Encoding(false)));
        }
        catch (Exception)
        {
            return [];
        }
        if (root is null) return [];
        if (root is JsonArray bare) return bare.ToList();

        foreach (var key in new[] { "profileList", "items", "ops" })
        {
            if (root[key] is JsonArray arr) return arr.ToList();
        }
        return [];
    }

    private static List<(string Uuid, string Name)> ReadNameUuidPairs(string path, string uuidKey, string nameKey)
    {
        var outList = new List<(string, string)>();
        foreach (var node in ReadArray(path))
        {
            var name = Str(node, nameKey);
            if (string.IsNullOrEmpty(name)) continue;   // 无名条目无法管理，跳过
            outList.Add((OfflinePlayerUuid.Normalize(Str(node, uuidKey)) ?? OfflinePlayerUuid.Compute(name!), name!));
        }
        return outList;
    }

    private static string? Str(JsonNode? node, string key)
    {
        if (node is not JsonObject o) return null;
        var v = o[key];
        if (v is null) return null;
        try
        {
            return v.GetValue<string>();
        }
        catch (Exception)
        {
            return v.ToString(); // 数字型 uuid 之类，退化成文本再交给 Normalize
        }
    }

    private static int Int(JsonNode? node, string key, int def)
    {
        if (node is not JsonObject o || o[key] is null) return def;
        try
        {
            return o[key]!.GetValue<int>();
        }
        catch (Exception)
        {
            return def;
        }
    }

    private static bool Bool(JsonNode? node, string key, bool def)
    {
        if (node is not JsonObject o || o[key] is null) return def;
        try
        {
            return o[key]!.GetValue<bool>();
        }
        catch (Exception)
        {
            return def;
        }
    }

    /// <summary>
    /// 写出名单文件：**裸 JSON 数组**，缩进两格，UTF-8 无 BOM。
    ///
    /// 为什么不能包一层 envelope：实测原版 26.3 的 <c>StoredUserList.load</c> 直接把文件
    /// 当 JSON 数组解析，喂它 <c>{"version":1,"profileList":[…]}</c> 会抛
    /// <c>JsonSyntaxException</c>，整份名单被当成空——界面上「已写入」，玩家却一个都进不来。
    /// whitelist / ops / banned-players / banned-ips 四种文件都是这个形态。
    /// </summary>
    private static void WriteArray(string path, JsonArray list)
    {
        var text = list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        try
        {
            // 原子写：temp + replace，避免服务端恰好在读时看到半个文件
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"写入名单文件失败: {path}", e);
        }
    }

    /// <summary>按 key 去重（保留首次出现），并过滤空 key。</summary>
    internal static List<T> Dedup<T>(IReadOnlyList<T> entries, Func<T, string> keySelector)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<T>();
        foreach (var e in entries)
        {
            var k = keySelector(e);
            if (string.IsNullOrEmpty(k) || !seen.Add(k)) continue;
            outList.Add(e);
        }
        return outList;
    }

    /// <summary>
    /// 当前时刻的 MC 时间戳格式（UTC，偏移 <c>+0000</c>）。
    /// public 是因为 GUI 的「玩家」页签要新建封禁条目（写 created 字段），
    /// 而它住在另一个程序集——不给 public 就只能让界面层自己拼时间格式，两处必然漂移。
    /// </summary>
    public static string NowStamp() =>
        // 手工拼 +0000：见 StampPattern 注释，.NET 没有对应 Java "Z" 的格式说明符
        DateTime.UtcNow.ToString(StampPattern, Inv) + " +0000";

    internal static long StampToMillis(string stamp)
    {
        var text = stamp.Trim();
        // 容错读入：Java 写的是无冒号 +0000，.NET 的 TryParseExact 需要 +00:00 形态
        var offsetAt = text.LastIndexOfAny(['+', '-']);
        if (offsetAt > 10) // 跳过日期里的 '-'（yyyy-MM-dd）
        {
            var offset = text[offsetAt..];
            if (offset.Length == 5) // +0000 → +00:00
            {
                text = text[..offsetAt] + offset[..3] + ":" + offset[3..];
            }
        }
        if (DateTimeOffset.TryParseExact(text, StampPattern + " zzz", Inv,
                DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }
        if (DateTimeOffset.TryParse(text, Inv, DateTimeStyles.AllowWhiteSpaces, out var loose))
        {
            return loose.ToUnixTimeMilliseconds();
        }
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
