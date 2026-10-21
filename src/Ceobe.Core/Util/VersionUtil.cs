using System.Text.RegularExpressions;

namespace Ceobe.Core.Util;

/// <summary>MC 版本号比较与归组：支持新纪年制（26.3）与旧制（1.21.11）。</summary>
public static partial class VersionUtil
{
    /// <summary>降序比较器：新版本在前（对齐 Java 侧 CMP）。</summary>
    public static IComparer<string> Descending { get; } = Comparer<string>.Create((a, b) =>
    {
        var x = Segments(a);
        var y = Segments(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var va = i < x.Length ? x[i] : 0;
            var vb = i < y.Length ? y[i] : 0;
            if (va != vb) return vb.CompareTo(va); // 降序
        }
        return 0;
    });

    /// <summary>26.3 / 1.21.4 这类纯数字点分视为正式版；含 -rc/-pre/-snapshot 的排除。</summary>
    public static bool IsRelease(string v) => ReleasePattern().IsMatch(v);

    /// <summary>家族分组键：26.3→"26.x"，1.21.4→"1.21.x"。</summary>
    public static string Family(string v)
    {
        var parts = v.Split('.');
        if (parts.Length <= 1) return v;
        return parts.Length == 2 ? parts[0] + ".x" : parts[0] + "." + parts[1] + ".x";
    }

    private static int[] Segments(string v)
    {
        var body = v.Split(['+', '-'], 2)[0];
        var parts = body.Split('.');
        var outArr = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            outArr[i] = int.TryParse(parts[i], out var n) ? n : 0;
        }
        return outArr;
    }

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex ReleasePattern();
}
