using System.IO.Compression;
using System.Text;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Util;

/// <summary>ZIP 解压：内容落地 + 目录穿越防护 + 递归复制。移植自 Java 版 ZipsTest。</summary>
public class ZipUtilTest
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ceobe-zip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact(DisplayName = "解压并拒绝目录穿越")]
    public void 解压并拒绝目录穿越()
    {
        var dir = TempDir();
        try
        {
            var zip = Path.Combine(dir, "a.zip");
            using (var fs = File.Create(zip))
            using (var outZip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(outZip, "ok/file.txt", "hi");
                // 恶意条目：必须被丢弃，不得写出 dest 之外
                WriteEntry(outZip, "../escape.txt", "bad");
            }

            var dest = Path.Combine(dir, "out");
            Directory.CreateDirectory(dest);
            ZipUtil.Extract(zip, dest);

            Assert.True(File.Exists(Path.Combine(dest, "ok", "file.txt")));
            Assert.Equal("hi", File.ReadAllText(Path.Combine(dest, "ok", "file.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "escape.txt")), "不得写出到 dest 之外");
        }
        finally
        {
            ZipUtil.DeleteRecursively(dir);
        }
    }

    [Fact(DisplayName = "Windows 风格的反斜杠穿越同样被拒")]
    public void 反斜杠穿越被拒()
    {
        var dir = TempDir();
        try
        {
            var zip = Path.Combine(dir, "b.zip");
            using (var fs = File.Create(zip))
            using (var outZip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(outZip, "ok\\file.txt", "hi");
                WriteEntry(outZip, "..\\escape.txt", "bad");
            }
            var dest = Path.Combine(dir, "out");
            Directory.CreateDirectory(dest);
            ZipUtil.Extract(zip, dest);

            Assert.True(File.Exists(Path.Combine(dest, "ok", "file.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "escape.txt")));
        }
        finally
        {
            ZipUtil.DeleteRecursively(dir);
        }
    }

    [Fact(DisplayName = "绝对路径条目不得逃出 dest")]
    public void 绝对路径条目被拒()
    {
        var dir = TempDir();
        try
        {
            var zip = Path.Combine(dir, "c.zip");
            var evil = Path.Combine(Path.GetTempPath(), "ceobe-evil-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            using (var fs = File.Create(zip))
            using (var outZip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(outZip, "ok/a.txt", "hi");
                WriteEntry(outZip, evil, "pwned");
            }
            var dest = Path.Combine(dir, "out");
            Directory.CreateDirectory(dest);
            ZipUtil.Extract(zip, dest);

            Assert.True(File.Exists(Path.Combine(dest, "ok", "a.txt")));
            Assert.False(File.Exists(evil), "绝对路径条目必须被拒");
        }
        finally
        {
            ZipUtil.DeleteRecursively(dir);
        }
    }

    [Fact(DisplayName = "递归复制到位")]
    public void 递归复制到位()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "x.txt"), "v");
            var dst = Path.Combine(dir, "dst");
            Directory.CreateDirectory(dst);

            Assert.Equal(1, ZipUtil.CopyInto(src, dst));
            Assert.Equal("v", File.ReadAllText(Path.Combine(dst, "sub", "x.txt")));
            Assert.Equal(0, ZipUtil.CopyInto(Path.Combine(dir, "missing"), dst)); // 源不存在返回 0
        }
        finally
        {
            ZipUtil.DeleteRecursively(dir);
        }
    }

    [Fact(DisplayName = "目录结构被保留（即使只有空目录）")]
    public void 空目录被保留()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "empty", "deep"));
            var dst = Path.Combine(dir, "dst");
            Assert.Equal(0, ZipUtil.CopyInto(src, dst)); // 无文件
            Assert.True(Directory.Exists(Path.Combine(dst, "empty", "deep")), "空目录应被创建");
        }
        finally
        {
            ZipUtil.DeleteRecursively(dir);
        }
    }

    [Fact(DisplayName = "递归删除对不存在路径不抛")]
    public void 递归删除容错()
    {
        var dir = TempDir();
        try
        {
            ZipUtil.DeleteRecursively(Path.Combine(dir, "nope")); // 不应抛
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            ZipUtil.DeleteRecursively(dir);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
