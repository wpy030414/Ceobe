namespace Ceobe.Core.Config;

/// <summary>
/// server.properties schema 基线 = vanilla 26.3 实测生成的键集（46 键），
/// 另含少量 1.21.x 旧键（标 Legacy，保证老配置不丢字段）。
/// 键集合、顺序、默认值与 Java 版逐条对齐（docs/MIGRATION.md 硬契约 1）；
/// 新增的 <c>Policy</c> 与 <c>Common</c> 为纯附加信息，不改变任何既有语义。
///
/// 关于 <see cref="RestartPolicy"/> 的诚实说明： vanilla 对「哪些键在运行期重读」没有公开契约，
/// 因此默认一律标 <see cref="RestartPolicy.Restart"/>（保守安全侧：多提示一次重启，
/// 远比「声称热生效而实际没生效」的静默失败好）。仅对**由服务端命令托管**的项标
/// <see cref="RestartPolicy.Reload"/>；待阶段一 E2E harness 实测后再扩充本表。
/// </summary>
public static class PropSchema
{
    public const string GBasic = "基础";
    public const string GRules = "游戏规则";
    public const string GWorld = "世界";
    public const string GNet = "网络";
    public const string GPerf = "性能";
    public const string GSec = "安全";
    public const string GAdv = "高级";

    private static readonly Dictionary<string, PropKey> Keys = new(StringComparer.Ordinal);
    private static readonly List<string> Order = [];

    /// <summary>整改项 5：「常用」视图的键集合（≤12 项，够开服即用）。</summary>
    private static readonly string[] CommonKeys =
    [
        "motd", "max-players", "gamemode", "difficulty", "white-list",
        "online-mode", "server-port", "view-distance", "simulation-distance",
        "level-seed", "pvp", "enable-command-block",
    ];

    static PropSchema()
    {
        // 基础
        Add(PropKey.String("motd", "服务器描述", GBasic, "A Minecraft Server"));
        Add(PropKey.Integer("max-players", "最大玩家数", GBasic, 20, 1, 10000));
        Add(PropKey.Enumerate("gamemode", "默认游戏模式", GBasic, "survival",
            options: ["survival", "creative", "adventure", "spectator"]));
        Add(PropKey.Enumerate("difficulty", "难度", GBasic, "easy",
            options: ["peaceful", "easy", "normal", "hard"]));
        Add(PropKey.Bool("force-gamemode", "强制游戏模式", GBasic, false));
        Add(PropKey.Bool("hardcore", "极限模式", GBasic, false));
        Add(PropKey.Bool("pvp", "允许 PVP", GBasic, true));
        Add(PropKey.Bool("require-resource-pack", "强制资源包", GBasic, false));
        Add(PropKey.String("resource-pack", "资源包 URL", GBasic, ""));
        Add(PropKey.String("resource-pack-id", "资源包 ID", GBasic, ""));
        Add(PropKey.String("resource-pack-prompt", "资源包提示", GBasic, ""));
        Add(PropKey.String("resource-pack-sha1", "资源包 SHA1", GBasic, ""));

        // 游戏规则
        Add(PropKey.Bool("allow-flight", "允许飞行", GRules, false));
        Add(PropKey.Bool("enable-command-block", "启用命令方块", GRules, false));
        Add(PropKey.Integer("spawn-protection", "出生点保护半径", GRules, 16, 0, 1000));
        Add(PropKey.Integer("player-idle-timeout", "挂机踢出分钟", GRules, 0, 0, 1440));
        Add(PropKey.Integer("chat-spam-threshold-seconds", "聊天限流窗口(秒)", GRules, 10, 0, 1000));
        Add(PropKey.Integer("command-spam-threshold-seconds", "命令限流窗口(秒)", GRules, 10, 0, 1000));
        Add(PropKey.Integer("op-permission-level", "OP 权限等级", GRules, 4, 1, 4));
        Add(PropKey.Integer("function-permission-level", "函数权限等级", GRules, 2, 1, 4));

        // 世界
        Add(PropKey.String("level-name", "世界名", GWorld, "world"));
        Add(PropKey.String("level-seed", "世界种子", GWorld, ""));
        Add(PropKey.String("level-type", "世界类型", GWorld, "minecraft:normal"));
        Add(PropKey.String("generator-settings", "自定义生成器设置", GWorld, "{}"));
        Add(PropKey.Bool("generate-structures", "生成结构", GWorld, true));
        Add(PropKey.Integer("max-world-size", "世界边界半径", GWorld, 29999984, 1, 29999984));
        Add(PropKey.String("initial-enabled-packs", "初始启用数据包", GWorld, "vanilla"));
        Add(PropKey.String("initial-disabled-packs", "初始禁用数据包", GWorld, ""));

        // 网络
        Add(PropKey.Integer("server-port", "服务器端口", GNet, 25565, 1, 65535));
        Add(PropKey.String("server-ip", "绑定 IP（空=全部）", GNet, ""));
        Add(PropKey.Bool("online-mode", "正版验证", GNet, true));
        Add(PropKey.Bool("enable-status", "在服务器列表显示", GNet, true));
        Add(PropKey.Bool("hide-online-players", "隐藏在线玩家", GNet, false));
        Add(PropKey.Bool("enable-query", "启用 Query 协议", GNet, false));
        Add(PropKey.Integer("query.port", "Query 端口", GNet, 25565, 1, 65535));
        Add(PropKey.Bool("enable-rcon", "启用 RCON", GNet, false));
        Add(PropKey.Integer("rcon.port", "RCON 端口", GNet, 25575, 1, 65535));
        Add(PropKey.String("rcon.password", "RCON 密码", GNet, ""));
        Add(PropKey.Integer("rate-limit", "单 IP 连接速率上限", GNet, 0, 0, 100000));
        Add(PropKey.Bool("prevent-proxy-connections", "阻止代理连接", GNet, false));
        Add(PropKey.Bool("use-native-transport", "使用原生传输优化", GNet, true));

        // 性能
        Add(PropKey.Integer("view-distance", "视距（区块）", GPerf, 10, 3, 32));
        Add(PropKey.Integer("simulation-distance", "模拟距离（区块）", GPerf, 10, 3, 32));
        Add(PropKey.Integer("entity-broadcast-range-percentage", "实体广播范围(%)", GPerf, 100, 10, 1000));
        Add(PropKey.Integer("network-compression-threshold", "网络压缩阈值", GPerf, 256, -1, 65536));
        Add(PropKey.Integer("max-tick-time", "单 tick 超时(ms)", GPerf, 60000, -1, 600000));
        Add(PropKey.Bool("sync-chunk-writes", "同步区块写入", GPerf, true));
        Add(PropKey.Integer("pause-when-empty-seconds", "空服暂停(秒)", GPerf, 60, 0, 3600));
        Add(PropKey.Enumerate("region-file-compression", "区域文件压缩", GPerf, "deflate",
            options: ["deflate", "lz4", "none"]));

        // 安全
        // 整改项 2：white-list 默认值在 C# 版将由「创建向导显式询问」接管，
        // schema 这里的 true 仅作为 properties 模板兜底，不代表产品默认开白名单。
        Add(PropKey.Bool("white-list", "启用白名单", GSec, true, RestartPolicy.Reload));
        Add(PropKey.Bool("enforce-whitelist", "强制白名单", GSec, false, RestartPolicy.Reload));
        Add(PropKey.Bool("enforce-secure-profile", "强制安全档案签名", GSec, true));
        Add(PropKey.Bool("log-ips", "记录玩家 IP", GSec, true));

        // 高级
        Add(PropKey.Bool("enable-jmx-monitoring", "启用 JMX 监控", GAdv, false));
        Add(PropKey.Bool("broadcast-console-to-ops", "控制台消息广播给 OP", GAdv, true));
        Add(PropKey.Bool("broadcast-rcon-to-ops", "RCON 消息广播给 OP", GAdv, true));
        Add(PropKey.Bool("accepts-transfers", "接受跨服转移", GAdv, false));
        Add(PropKey.Bool("enable-code-of-conduct", "启用行为准则", GAdv, false));
        Add(PropKey.String("bug-report-link", "问题反馈链接", GAdv, ""));
        Add(PropKey.Integer("max-chained-neighbor-updates", "连锁方块更新上限", GAdv, 1000000, 0, int.MaxValue));
        Add(PropKey.Integer("status-heartbeat-interval", "状态心跳间隔", GAdv, 0, 0, 3600));
        Add(PropKey.String("text-filtering-config", "文本过滤配置", GAdv, ""));
        Add(PropKey.Integer("text-filtering-version", "文本过滤版本", GAdv, 0, 0, int.MaxValue));
        Add(PropKey.Bool("management-server-enabled", "启用管理服务端 API", GAdv, false));
        Add(PropKey.String("management-server-host", "管理 API 绑定", GAdv, "localhost"));
        Add(PropKey.Integer("management-server-port", "管理 API 端口", GAdv, 0, 0, 65535));
        Add(PropKey.String("management-server-secret", "管理 API 密钥", GAdv, ""));
        Add(PropKey.Bool("management-server-tls-enabled", "管理 API 启用 TLS", GAdv, true));
        Add(PropKey.String("management-server-tls-keystore", "管理 API 密钥库", GAdv, ""));
        Add(PropKey.String("management-server-tls-keystore-password", "管理 API 密钥库口令", GAdv, ""));
        Add(PropKey.String("management-server-allowed-origins", "管理 API 允许来源", GAdv, ""));

        // 旧版本键（Legacy，1.21.x 及更早可能仍存在）
        Add(PropKey.Bool("spawn-monsters", "生成怪物", GRules, true).AsLegacy());
        Add(PropKey.Bool("spawn-animals", "生成动物", GRules, true).AsLegacy());
        Add(PropKey.Bool("spawn-npcs", "生成村民", GRules, true).AsLegacy());
        Add(PropKey.Bool("spawn-water-animals", "生成水生生物", GRules, true).AsLegacy());
        Add(PropKey.Bool("allow-nether", "允许下界", GWorld, true).AsLegacy());
        Add(PropKey.Bool("previews-chat", "聊天预览", GAdv, false).AsLegacy());
        Add(PropKey.Bool("enable-command-block", "启用命令方块", GRules, false).AsLegacy());
    }

    /// <summary>对齐 Java 侧 putIfAbsent 语义：重复键名保留首次定义。</summary>
    private static void Add(PropKey key)
    {
        if (Keys.ContainsKey(key.Key)) return;
        Keys[key.Key] = key;
        Order.Add(key.Key);
    }

    public static PropKey? Key(string name) => Keys.GetValueOrDefault(name);

    public static IReadOnlyList<PropKey> All() => [.. Order.Select(k => Keys[k])];

    /// <summary>当前版本（非 legacy）的分组顺序。</summary>
    public static IReadOnlyList<string> Groups() => [GBasic, GRules, GWorld, GNet, GPerf, GSec, GAdv];

    /// <summary>整改项 5：「常用」视图条目（保持 schema 顺序，仅取白名单内且非 legacy 的键）。</summary>
    public static IReadOnlyList<PropKey> Common() =>
        [.. Order.Select(k => Keys[k]).Where(k => !k.Legacy && CommonKeys.Contains(k.Key))];

    /// <summary>指定分组下的非 legacy 条目。</summary>
    public static IReadOnlyList<PropKey> InGroup(string group) =>
        [.. Order.Select(k => Keys[k]).Where(k => !k.Legacy && k.Group == group)];

    /// <summary>界面与原始表用：按 key 名或中文名模糊匹配。</summary>
    public static IReadOnlyList<PropKey> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All();
        var q = query.Trim();
        return
        [
            .. Order.Select(k => Keys[k])
                .Where(k => k.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || k.Zh.Contains(q, StringComparison.Ordinal))
        ];
    }
}
