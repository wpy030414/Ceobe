namespace Ceobe.Core.Util;

/// <summary>统一业务异常：message 面向用户可直接展示。</summary>
public class CeobeException : Exception
{
    public CeobeException(string message) : base(message) { }

    public CeobeException(string message, Exception cause) : base(message, cause) { }

    /// <summary>
    /// 不变文化格式化（对齐 Java 的 formatted，避免受当前区域设置影响）。
    /// 参数刻意声明为可空：调用方常把「可能为 null 的值」直接塞进消息
    /// （如 ServerType.FromLoaderId(null) 要报出「不支持 null 加载器」），
    /// 而 string.Format 本就支持 null 参数并输出空串。若声明成非空，
    /// 每个这类调用点都会冒 CS8604，把真正的可空性 bug 埋进噪音。
    /// </summary>
    public static CeobeException Of(string fmt, params object?[] args) =>
        new(string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args));
}

/// <summary>
/// 面向用户的参数校验错误（用法错误，非系统故障）。
/// 与 Java 侧 <c>InstanceSpec.CeobeSpec</c> 对齐：刻意不继承 <see cref="CeobeException"/>，
/// 便于 CLI 区分「用法错误」（退出码 2）与「系统故障」（退出码 1）。
/// </summary>
public class CeobeSpec(string message) : Exception(message);
