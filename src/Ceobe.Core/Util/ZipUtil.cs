using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Ceobe.Core.Util;

/// <summary>ZIP 解压（含目录穿越防护）与递归删除。</summary>
public static partial class ZipUtil
{
    /// <summary>解压到 dest（自动建目录）；拒绝逃逸出 dest 的条目。</summary>
    public static void Extract(string zipPath, string dest)
    {
        var fullDest = System.IO.Path.GetFullPath(dest);
        Directory.CreateDirectory(fullDest);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && !entry.FullName.EndsWith('/')) continue;
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullDest, entry.FullName));
                if (!IsWithin(target, fullDest)) continue; // 防目录穿越
                if (entry.FullName.EndsWith("/") || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new CeobeException($"解压失败: {zipPath} ({e.Message})", e);
        }
    }

    /// <summary>递归复制目录内容到 dest（目录不存在则跳过），返回复制的文件数。</summary>
    public static int CopyInto(string source, string dest)
    {
        if (!Directory.Exists(source)) return 0;
        var fullDest = System.IO.Path.GetFullPath(dest);
        var count = 0;
        try
        {
            foreach (var p in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = System.IO.Path.GetRelativePath(source, p);
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullDest, rel));
                if (!IsWithin(target, fullDest)) continue;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(p, target, overwrite: true);
                count++;
            }
            foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                var rel = System.IO.Path.GetRelativePath(source, d);
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullDest, rel));
                if (IsWithin(target, fullDest)) Directory.CreateDirectory(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"复制目录失败: {source}", e);
        }
        return count;
    }

    public static void DeleteRecursively(string root)
    {
        if (!Directory.Exists(root) && !File.Exists(root)) return;
        try
        {
            if (File.Exists(root)) { File.Delete(root); return; }
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* 尽力而为，对齐 Java 侧静默语义 */ }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsWithin(string candidate, string root)
    {
        var c = TrimSep(candidate);
        var r = TrimSep(root);
        return c.Equals(r, comparison) || c.StartsWith(r + System.IO.Path.DirectorySeparatorChar, comparison);
    }

    private static string TrimSep(string p) =>
        p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    private static readonly StringComparison comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
