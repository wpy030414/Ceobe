namespace Ceobe.Core.Util;

/// <summary>统一业务异常：message 面向用户可直接展示。</summary>
public class CeobeException : Exception
{
    public CeobeException(string message) : base(message) { }

    public CeobeException(string message, Exception cause) : base(message, cause) { }

    /// <summary>不变文化格式化（对齐 Java 的 formatted，避免受当前区域设置影响）。</summary>
    public static CeobeException Of(string fmt, params object[] args) =>
        new(string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args));
}

/// <summary>
/// 面向用户的参数校验错误（用法错误，非系统故障）。
/// 与 Java 侧 <c>InstanceSpec.CeobeSpec</c> 对齐：刻意不继承 <see cref="CeobeException"/>，
/// 便于 CLI 区分「用法错误」（退出码 2）与「系统故障」（退出码 1）。
/// </summary>
public class CeobeSpec(string message) : Exception(message);
