using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// <c>options.txt</c> 的保序读写。
///
/// 硬约束（与 <c>server.properties</c> 的保序/保注释是同一类问题）：
/// 游戏自己会往这个文件里写几十个键，用户也可能手改过。启动器只该动它关心的那几行，
/// 其余必须**原样保留**——顺序、空白、乃至我们不认识的键。用「读成字典再整体重写」
/// 的做法会丢掉顺序，而 <c>resourcePacks</c> 的语义恰恰依赖顺序（越靠前优先级越高）。
///
/// 因此这里保留原始行，只对命中的键做原地替换；文件末尾缺换行也照原样还原。
/// </summary>
public sealed class OptionsTxt
{
    private readonly List<string> _lines = [];
    private readonly string _path;
    private bool _trailingNewline;

    private OptionsTxt(string path) => _path = path;

    public static OptionsTxt Load(string path)
    {
        var file = new OptionsTxt(path);
        if (!File.Exists(path)) return file;
        try
        {
            var text = File.ReadAllText(path, new UTF8Encoding(false));
            file._trailingNewline = text.EndsWith('\n');
            foreach (var line in text.Split('\n'))
            {
                // 末行（空串）不当作一行内容
                if (line.Length == 0) continue;
                file._lines.Add(line.TrimEnd('\r'));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"读取 options.txt 失败: {path}", e);
        }
        return file;
    }

    public bool Contains(string key) => IndexOf(key) >= 0;

    public string? Get(string key)
    {
        var i = IndexOf(key);
        if (i < 0) return null;
        var line = _lines[i];
        var colon = line.IndexOf(':');
        return colon < 0 ? "" : line[(colon + 1)..];
    }

    public void Set(string key, string value)
    {
        var i = IndexOf(key);
        if (i >= 0) _lines[i] = key + ":" + value;
        else _lines.Add(key + ":" + value);
    }

    public bool GetBool(string key, bool fallback)
    {
        var raw = Get(key);
        return raw is null ? fallback : string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读 <c>["a","b"]</c> 形式的数组值。游戏写的是 JSON 数组字符串，
    /// 但历史上也有手改坏的情况（缺引号、多余空格），故解析失败时退回「按逗号硬切」。
    /// </summary>
    public IReadOnlyList<string> GetArray(string key)
    {
        var raw = Get(key);
        return raw is null ? [] : ParseArray(raw);
    }

    public void SetArray(string key, IEnumerable<string> values) => Set(key, FormatArray(values));

    public void Remove(string key)
    {
        var i = IndexOf(key);
        if (i >= 0) _lines.RemoveAt(i);
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var text = string.Join("\n", _lines) + (_trailingNewline || _lines.Count == 0 ? "\n" : "");
            File.WriteAllText(_path, text, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"写入 options.txt 失败: {_path}", e);
        }
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Length == 0 || line[0] == '#') continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (string.Equals(line[..colon], key, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    public static IReadOnlyList<string> ParseArray(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return [];
        if (s[0] == '[' && s[^1] == ']') s = s[1..^1];
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    inQuote = !inQuote;
                    break;
                case ',' when !inQuote:
                    // 手改过的文件里常见 ["a" , "b"]：元素两侧空白不属于值
                    if (sb.ToString().Trim().Length > 0) result.Add(sb.ToString().Trim());
                    sb.Clear();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        if (sb.ToString().Trim().Length > 0) result.Add(sb.ToString().Trim());
        return result;
    }

    public static string FormatArray(IEnumerable<string> values)
    {
        var items = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        return "[" + string.Join(",", items) + "]";
    }
}
