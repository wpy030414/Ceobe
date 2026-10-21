using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ceobe.Core.Config;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 配置表单的单个字段（可绑定投影）。
///
/// 为什么不是 record：值变化要能被 XAML 双向绑定感知，且校验错误要就地显示。
/// 校验**在赋值时立即执行**（对齐 Java 版 markDirty 前先 PropKey.validate 的顺序），
/// 但校验失败不回滚值——用户正在打字，回滚会让输入框跳字，改为标红 + 阻止记入 pending。
/// </summary>
public partial class PropField : ObservableObject
{
    private readonly PropKey? _schema;
    private readonly Action<PropField, string>? _onChange;
    private string _value = "";

    private PropField(PropKey? schema, string key, string zh, string group, PropType type,
                      string value, bool isUnknown, Action<PropField, string>? onChange)
    {
        _schema = schema;
        _onChange = onChange;
        Key = key;
        Zh = zh;
        Group = group;
        Type = type;
        IsUnknown = isUnknown;
        _value = value;
        // 构造即捕获原值：IsModified 靠它判断，否则每个字段会永远显示「已改动」。
        OriginalValue = value;
    }

    /// <summary>schema 收录的键。</summary>
    public static PropField FromSchema(PropKey schema, string value, Action<PropField, string> onChange) =>
        new(schema, schema.Key, schema.Zh, schema.Group, schema.Type, value, false, onChange);

    /// <summary>未收录键（旧版本 / 插件写入）：只读，但仍要让用户看见。</summary>
    public static PropField Unknown(string key, string value, Action<PropField, string> onChange) =>
        new(null, key, key, "其他", PropType.String, value, true, onChange);

    /// <summary>properties 里的原始键名（等宽字体展示）。</summary>
    public string Key { get; }

    /// <summary>中文友好名。</summary>
    public string Zh { get; }

    /// <summary>所属分组（分类视图导航用）。</summary>
    public string Group { get; }

    public PropType Type { get; }

    /// <summary>是否未收录键（只读）。</summary>
    public bool IsUnknown { get; }

    /// <summary>枚举可选值。</summary>
    public IReadOnlyList<string> Options => _schema?.Options ?? [];

    /// <summary>Int 的上下限（已做 Java 版的哨兵值收敛）。</summary>
    public long Min => _schema is null ? 0 : (_schema.Min == long.MinValue ? 0 : _schema.Min);
    public long Max => _schema is null ? 1_000_000 : (_schema.Max == long.MaxValue ? 1_000_000 : _schema.Max);

    /// <summary>schema 默认值（「恢复默认」按钮用）。</summary>
    public string Def => _schema?.Def ?? "";

    /// <summary>是否允许编辑（未收录键只读）。</summary>
    public bool IsEditable => !IsUnknown;

    // —— 类型判断（纯 bool，供 View 层按类型分派控件；刻意不用 WinUI 的 Visibility，
    //    保持 VM 层零 UI 依赖，可无窗口单测——闸门 C）——

    /// <summary>布尔开关（ToggleSwitch/CheckBox）。未收录键不套用类型控件，统一走只读文本。</summary>
    public bool IsBool => !IsUnknown && Type == PropType.Bool;

    /// <summary>整数（NumberBox）。</summary>
    public bool IsInt => !IsUnknown && Type == PropType.Int;

    /// <summary>枚举（ComboBox）。</summary>
    public bool IsEnum => !IsUnknown && Type == PropType.Enum;

    /// <summary>文本框：String 类型，或未收录键（只读展示）。</summary>
    public bool IsString => !IsBool && !IsInt && !IsEnum;

    /// <summary>只读（未收录键）。View 层据此把 TextBox 设为只读。</summary>
    public bool IsReadOnly => IsUnknown;

    /// <summary>
    /// 生效方式文案（整改项 5）。Java 版没有这个信息，用户改完不知道要不要重启——
    /// 「改了没反应」是配置界面最伤信任的体验，故把它做成一等公民。
    /// </summary>
    public string PolicyLabel => IsUnknown ? "" : (_schema?.Policy ?? RestartPolicy.Unknown) switch
    {
        RestartPolicy.Restart => "需重启",
        RestartPolicy.Reload => "可热重载",
        RestartPolicy.Live => "立即生效",
        _ => "未知（按需重启）",
    };

    /// <summary>是否改完必须重启服务端（界面据此决定提示强度）。</summary>
    public bool RequiresRestart => !IsUnknown && _schema?.Policy == RestartPolicy.Restart;

    /// <summary>范围提示文案（对齐 Java 版 "范围 min – max"）。</summary>
    public string RangeHint => Type == PropType.Int ? $"范围 {Min} – {Max}" : "";

    /// <summary>当前值。赋值即校验；校验失败置 <see cref="ValidationError"/> 且不通知外层记录改动。</summary>
    public string Value
    {
        get => _value;
        set
        {
            // ComboBox 的 SelectedItem 在 ItemsSource 初始化阶段可能先推一个 null；
            // 这不是用户输入，交给 Validate 会 NRE。保持原值即可。
            if (value is null) return;

            if (_value == value) return;

            if (IsUnknown) return; // 只读：静默忽略，避免绑定回写造成困惑

            string? error = null;
            if (_schema is not null)
            {
                try
                {
                    _schema.Validate(value);
                }
                catch (ArgumentException e)
                {
                    error = e.Message;
                }
            }
            ValidationError = error;

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
            // 校验失败也要更新界面显示（用户在打字），但不记入 pending，
            // 否则会把非法值写进 server.properties
            if (error is null) _onChange?.Invoke(this, value);
        }
    }

    /// <summary>校验错误文案；null = 合法。</summary>
    [ObservableProperty]
    private string? _validationError;

    /// <summary>是否已被改动（界面据此标记「未保存」小圆点）。</summary>
    public bool IsModified => _schema is not null && !string.Equals(_value, OriginalValue, StringComparison.Ordinal);

    /// <summary>加载时的原值（供 <see cref="IsModified"/> 与「恢复默认」对比），构造时捕获。</summary>
    public string OriginalValue { get; }

    /// <summary>
    /// 搜索匹配（整改项 5 的分类视图搜索）：按 key 名与中文标签模糊匹配。
    /// 中文用 Ordinal 比较（不做大小写折叠），英文 key 忽略大小写。
    /// </summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return Key.Contains(q, StringComparison.OrdinalIgnoreCase)
               || Zh.Contains(q, StringComparison.Ordinal)
               || Group.Contains(q, StringComparison.Ordinal);
    }

    /// <summary>恢复该字段的 schema 默认值。</summary>
    public void ResetToDefault()
    {
        if (_schema is null) return;
        Value = _schema.Def;
    }

    /// <summary>Int 字段的数值形态（供 NumberBox 绑定）；非法值返回 null。</summary>
    public long? NumberValue
    {
        get => long.TryParse(_value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        set
        {
            if (value is null) return;
            Value = value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    // WinUI 的 NumberBox 的 Value/Minimum/Maximum 都是 double，直接绑 long 会报 WMC1121
    // （类型不匹配）。故另给 double 形态。double 是基础类型、非 UI 类型，VM 层仍可无窗口单测。
    // NumberBox.Value 不可空，无值时给下限，避免绑定 null 触发控件异常。

    /// <summary>NumberBox.Value 用的 double 形态。</summary>
    public double NumberValueDouble
    {
        get => NumberValue ?? Min;
        set => Value = ((long)value).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>NumberBox.Minimum 用的 double 形态。</summary>
    public double MinDouble => Min;

    /// <summary>NumberBox.Maximum 用的 double 形态。</summary>
    public double MaxDouble => Max;

    /// <summary>Bool 字段的勾选形态（供 ToggleSwitch 绑定）。</summary>
    public bool BoolValue
    {
        get => string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
}
