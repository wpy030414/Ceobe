namespace Ceobe.Core.Config;

/// <summary>配置项类型：决定 GUI 渲染的控件形态。</summary>
public enum PropType
{
    Bool,
    Int,
    Enum,
    String,
}

/// <summary>修改该项后是否需要重启服务端（整改项 5，阶段二界面与日志共用此标记）。</summary>
public enum RestartPolicy
{
    /// <summary>写入后需重启服务端才生效。</summary>
    Restart,

    /// <summary>可经服务端命令热生效（如 whitelist reload / diff）。</summary>
    Reload,

    /// <summary>立即生效，无需任何操作。</summary>
    Live,

    /// <summary>不确定：保守按需重启处理。</summary>
    Unknown,
}

/// <summary>
/// server.properties 单个键的 schema 描述。
/// </summary>
/// <param name="Key">键名（JSON/文件中的原始 key）</param>
/// <param name="Zh">中文友好名</param>
/// <param name="Group">分组（基础/游戏规则/世界/网络/性能/安全/高级）</param>
/// <param name="Type">类型</param>
/// <param name="Def">默认值（字符串原样）</param>
/// <param name="Options">ENUM 的可选值</param>
/// <param name="Min">Int 下限</param>
/// <param name="Max">Int 上限</param>
/// <param name="Legacy">是否为旧版本键（当前版本已不再生成）</param>
/// <param name="Policy">生效方式（整改项 5）</param>
public sealed record PropKey(
    string Key,
    string Zh,
    string Group,
    PropType Type,
    string Def,
    IReadOnlyList<string> Options,
    long Min,
    long Max,
    bool Legacy,
    RestartPolicy Policy)
{
    private const long NoMin = long.MinValue;
    private const long NoMax = long.MaxValue;

    public static PropKey Bool(string key, string zh, string group, bool def,
                               RestartPolicy policy = RestartPolicy.Restart) =>
        new(key, zh, group, PropType.Bool, def ? "true" : "false", [], NoMin, NoMax, false, policy);

    public static PropKey Integer(string key, string zh, string group, long def, long min, long max,
                                  RestartPolicy policy = RestartPolicy.Restart) =>
        new(key, zh, group, PropType.Int, def.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [], min, max, false, policy);

    public static PropKey Enumerate(string key, string zh, string group, string def,
                                    RestartPolicy policy = RestartPolicy.Restart,
                                    params string[] options) =>
        new(key, zh, group, PropType.Enum, def, options, NoMin, NoMax, false, policy);

    public static PropKey String(string key, string zh, string group, string def,
                                 RestartPolicy policy = RestartPolicy.Restart) =>
        new(key, zh, group, PropType.String, def, [], NoMin, NoMax, false, policy);

    /// <summary>标记为旧版本键（保留以兼容老配置文件）。</summary>
    public PropKey AsLegacy() => this with { Legacy = true };

    /// <summary>用户输入校验；抛 <see cref="ArgumentException"/> 携带中文原因。</summary>
    public void Validate(string value)
    {
        switch (Type)
        {
            case PropType.Bool:
                if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"{Zh} 必须是 true/false");
                break;
            case PropType.Int:
                if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    throw new ArgumentException($"{Zh} 必须是整数");
                if (v < Min || v > Max)
                    throw new ArgumentException($"{Zh} 需在 {Min}-{Max} 之间");
                break;
            case PropType.Enum:
                if (!Options.Contains(value, StringComparer.Ordinal))
                    throw new ArgumentException($"{Zh} 只能是 {string.Join("/", Options)}");
                break;
            case PropType.String:
                break;
        }
    }
}
