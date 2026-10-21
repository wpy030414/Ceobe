using System.Globalization;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli;

/// <summary>
/// CLI 参数解析辅助。逐条照搬 Java 版 <c>InstanceCommands</c> 的静态解析方法——
/// 这些是用户可见的输入契约（<c>--mem 4G</c>、<c>--port auto</c>），改一个字符就是 breaking change。
/// </summary>
public static class CliParse
{
    /// <summary>vanilla / paper / fabric / neoforge / purpur（大小写不敏感）。</summary>
    public static ServerType Type(string raw)
    {
        if (Enum.TryParse<ServerType>(raw.Trim(), ignoreCase: true, out var t)) return t;
        throw new CeobeException(
            $"未知服务端类型: {raw}（可选 vanilla/paper/fabric/neoforge/purpur）");
    }

    /// <summary>解析为 1-65535 的整数（不接受 auto）。</summary>
    public static int Port(string raw)
    {
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            && p is >= 1 and <= 65535)
        {
            return p;
        }
        throw new CeobeException($"端口格式错误: {raw}（应为 1-65535 的整数或 auto）");
    }

    /// <summary>"auto"/空 → 自动选取空闲端口；否则解析为 1-65535 的整数。</summary>
    public static int ResolvePort(string? raw, InstanceManager manager)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return manager.SuggestFreePort(25565);
        }
        return Port(s);
    }

    /// <summary>"4G" → 4096；"4096M"/"4096" → 4096。</summary>
    public static int Memory(string raw)
    {
        var s = raw.Trim().ToUpperInvariant();
        try
        {
            if (s.EndsWith('G'))
            {
                return int.Parse(s[..^1], CultureInfo.InvariantCulture) * 1024;
            }
            if (s.EndsWith('M')) return int.Parse(s[..^1], CultureInfo.InvariantCulture);
            return int.Parse(s, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            throw new CeobeException($"内存格式错误: {raw}（示例 4G / 4096M）");
        }
    }

    /// <summary>可空的内存解析（update 的 --mem 未传时应为 null 而非报错）。</summary>
    public static int? MemoryOrNull(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : Memory(raw);

    /// <summary>可空的端口解析：null / auto / 数字。</summary>
    public static int? PortOrNull(string? raw, InstanceManager manager)
    {
        if (raw is null) return null;
        return ResolvePort(raw, manager);
    }

    /// <summary>
    /// 下载进度渲染：同一行 \r 刷新，700ms 节流（对齐 Java 版，避免刷屏）。
    /// json 模式下完全静默，否则会污染机器可读输出。
    /// </summary>
    public static IProgressSink ProgressBar() => new ConsoleProgressBar();

    private sealed class ConsoleProgressBar : IProgressSink
    {
        private long _lastPrint;

        public void Update(string stage, long done, long total)
        {
            if (Output.Json) return;
            var now = Environment.TickCount64;
            if (now - _lastPrint < 700 && total != done) return;
            _lastPrint = now;
            if (total > 0)
            {
                Console.Write("\r{0,-40} {1}%", Truncate(stage, 40), done * 100 / total);
            }
            else
            {
                Console.Write("\r{0,-40}", Truncate(stage, 40));
            }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>进度条收尾（换行），json 模式下不做。</summary>
    public static void ProgressDone()
    {
        if (!Output.Json) Console.WriteLine();
    }
}
