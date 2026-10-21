using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Core.Config;

/// <summary>
/// server.properties 读写：保序、保注释、保转义（AGENTS 硬契约 / docs/MIGRATION.md 硬契约 1）。
///
/// 读：逐行保留原文；语义值按 Java Properties 转义规则反解。
/// 写：仅重写被 set 的键所在行（原地替换），新增键追加至末尾；其余字节原样。
///
/// C# 侧对 Java 版的两处忠实性修正：
/// 1. 行尾风格从原文件探测（\r\n 或 \n）并沿用，避免整文件字节被换行符改写；
/// 2. 原文件末尾是否有换行符也被保留。
/// </summary>
public sealed class ServerPropertiesFile
{
    private readonly string _file;
    private readonly List<string> _lines = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lineIndexOf = new(StringComparer.Ordinal);
    private string _newline = "\n";
    private bool _trailingNewline = true;
    private bool _dirty;

    private ServerPropertiesFile(string file) => _file = file;

    public static ServerPropertiesFile Load(string file)
    {
        var p = new ServerPropertiesFile(file);
        if (!File.Exists(file)) return p;

        string raw;
        try
        {
            raw = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"读取配置失败: {file}", e);
        }

        p._newline = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        p._trailingNewline = raw.Length == 0 || raw.EndsWith('\n');
        // 与 Java Files.readAllLines 严格对齐：空行也是行，必须保留（不得用 RemoveEmptyEntries）
        var parts = raw.Split('\n');
        if (raw.Length > 0 && raw.EndsWith('\n')) parts = parts[..^1];
        foreach (var line in parts)
        {
            p._lines.Add(line.EndsWith('\r') ? line[..^1] : line);
        }

        for (var i = 0; i < p._lines.Count; i++)
        {
            var line = p._lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '!') continue;
            var sep = IndexOfSeparator(line);
            if (sep < 0) continue;
            var key = line[..sep].Trim();
            var value = Unescape(line[(sep + 1)..]);
            p._values[key] = value;
            p._lineIndexOf[key] = i;
        }
        return p;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public string GetOrDefault(string key) =>
        _values.TryGetValue(key, out var v) ? v : PropSchema.Key(key)?.Def ?? string.Empty;

    public bool Contains(string key) => _values.ContainsKey(key);

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>空值不污染界面：过滤掉解析失败的伪键。</summary>
    public void Set(string key, string value)
    {
        var schema = PropSchema.Key(key);
        if (schema is not null)
        {
            try
            {
                schema.Validate(value);
            }
            catch (ArgumentException e)
            {
                throw new CeobeException(e.Message!);
            }
        }
        var encoded = Escape(value);
        _values[key] = value;
        if (_lineIndexOf.TryGetValue(key, out var idx))
        {
            _lines[idx] = key + "=" + encoded;
        }
        else
        {
            _lines.Add(key + "=" + encoded);
            _lineIndexOf[key] = _lines.Count - 1;
        }
        _dirty = true;
    }

    public bool IsDirty => _dirty;

    /// <summary>原子写回（temp + replace）。</summary>
    public void Save()
    {
        if (!_dirty) return;
        var sb = new StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            sb.Append(_lines[i]);
            if (i < _lines.Count - 1 || _trailingNewline) sb.Append(_newline);
        }
        var tmp = _file + ".tmp";
        try
        {
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw new CeobeException($"写入配置失败: {_file}", e);
        }
    }

    /// <summary>新建实例用的模板（仅非 legacy 键，顺序同 schema）。</summary>
    public static string Template()
    {
        var sb = new StringBuilder("#Minecraft server properties\n");
        foreach (var k in PropSchema.All())
        {
            if (k.Legacy) continue;
            sb.Append(k.Key).Append('=').Append(Escape(k.Def)).Append('\n');
        }
        return sb.ToString();
    }

    // —— 转义（对齐 Java Properties 语义）——

    /// <summary>找未被反斜杠转义的第一个分隔符（= 或 :）。</summary>
    private static int IndexOfSeparator(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\') { i++; continue; }
            if (c == '=' || c == ':') return i;
        }
        return -1;
    }

    internal static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            var n = s[++i];
            switch (n)
            {
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < s.Length)
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                        i += 4;
                    }
                    break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ':': sb.Append("\\:"); break;
                case '=': sb.Append("\\="); break;
                case '#': sb.Append("\\#"); break;
                case '!': sb.Append("\\!"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
