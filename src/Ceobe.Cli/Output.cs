using System.Globalization;
using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Cli;

/// <summary>
/// CLI 输出：默认人类表格，<c>--format json</c> 输出机器可读（对齐 Java 版 Output）。
///
/// 中文对齐细节：表格宽度按**显示宽度**而非字符数计算，中日韩字符占 2 列——
/// 否则中文表头会把整张表撑歪。这段逻辑 Java 版踩过坑，故逐条照搬。
/// </summary>
public static class Output
{
    /// <summary>json 模式开关（由根命令的 --format 选项设置，进程内共享）。</summary>
    public static bool Json;

    /// <summary>输出 UTF-8：Windows 控制台默认非 UTF-8，中文会乱码（Java 版在 main 里做同一件事）。</summary>
    public static void ConfigureEncoding()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // 输出被重定向到管道时设置编码可能失败，不致命
        }
    }

    public static void JsonValue(object value) => Console.WriteLine(JsonUtil.Write(value));

    /// <summary>渲染两列以上的表格，自动按显示宽度对齐。</summary>
    public static void Table(string[] headers, List<string[]> rows)
    {
        var cols = headers.Length;
        var widths = new int[cols];
        for (var i = 0; i < cols; i++) widths[i] = DisplayWidth(headers[i]);
        foreach (var row in rows)
        {
            for (var i = 0; i < cols; i++)
            {
                widths[i] = Math.Max(widths[i], DisplayWidth(i < row.Length ? row[i] ?? "" : ""));
            }
        }

        var head = new StringBuilder();
        var sep = new StringBuilder();
        for (var i = 0; i < cols; i++)
        {
            head.Append(Pad(headers[i], widths[i])).Append(i < cols - 1 ? "  " : "");
            sep.Append(new string('-', widths[i])).Append(i < cols - 1 ? "  " : "");
        }
        Console.WriteLine(head.ToString().TrimEnd());
        Console.WriteLine(sep.ToString().TrimEnd());

        foreach (var row in rows)
        {
            var line = new StringBuilder();
            for (var i = 0; i < cols; i++)
            {
                line.Append(Pad(i < row.Length ? row[i] ?? "" : "", widths[i]))
                    .Append(i < cols - 1 ? "  " : "");
            }
            Console.WriteLine(line.ToString().TrimEnd());
        }
    }

    /// <summary>人类可读信息（json 模式下静默，避免污染机器可读输出）。</summary>
    public static void Info(string message)
    {
        if (!Json) Console.WriteLine(message);
    }

    public static void Info(string fmt, params object?[] args)
    {
        if (!Json) Console.WriteLine(string.Format(CultureInfo.InvariantCulture, fmt, args));
    }

    /// <summary>非致命提醒（json 模式下静默）。</summary>
    public static void Warn(string fmt, params object?[] args)
    {
        if (!Json) Console.Error.WriteLine("警告: " + string.Format(CultureInfo.InvariantCulture, fmt, args));
    }

    /// <summary>错误信息（json 模式下也输出到 stderr——错误不是数据）。</summary>
    public static void Error(string message) => Console.Error.WriteLine("错误: " + message);

    /// <summary>
    /// 显示宽度：CJK 与全角字符按 2 列计。范围取自 Java 版 Output.isWide，逐条照搬。
    /// </summary>
    internal static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var c in s)
        {
            w += IsWide(c) ? 2 : 1;
        }
        return w;
    }

    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ'
        or >= '⺀' and <= '꓏'
        or >= '가' and <= '힣'
        or >= '豈' and <= '﫿'
        or >= '︰' and <= '﹯'
        or >= '＀' and <= '｠'
        or >= '￠' and <= '￦';

    private static string Pad(string s, int width)
    {
        var diff = width - DisplayWidth(s);
        return diff <= 0 ? s : s + new string(' ', diff);
    }

    /// <summary>构造有序字典（等价 Java 版 Output.map，用于 json 输出的键序稳定）。</summary>
    public static Dictionary<string, object?> Map(params object?[] kv)
    {
        var m = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < kv.Length; i += 2)
        {
            m[kv[i]?.ToString() ?? ""] = kv[i + 1];
        }
        return m;
    }
}
