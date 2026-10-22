using System.IO.Compression;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>存档（世界）的一项。</summary>
public sealed record SaveEntry(
    string Folder,
    string LevelName,
    string GameMode,
    DateTimeOffset? LastPlayed,
    long SizeBytes,
    bool Hardcore,
    string? VersionName,
    string? IconPath);

/// <summary>
/// <c>saves/</c> 的世界列表与备份。
///
/// 世界名、游戏模式、最后游玩时间都在 <c>level.dat</c>（gzip NBT）里，目录名往往是
/// <c>New World</c> / <c>World-2026</c> 这类无意义字符串。启动器如果只显示目录名，
/// 用户根本认不出哪个是自己玩了两百小时的世界——所以这里必须读 level.dat。
/// </summary>
public static class SaveCatalog
{
    public static IReadOnlyList<SaveEntry> List(string clientName)
    {
        var dir = ClientStore.SavesDir(clientName);
        var result = new List<SaveEntry>();
        try
        {
            foreach (var worldDir in Directory.EnumerateDirectories(dir))
            {
                var levelDat = Path.Combine(worldDir, "level.dat");
                if (!File.Exists(levelDat)) continue;   // 没有 level.dat 的不是世界
                var entry = Read(worldDir, levelDat);
                if (entry is not null) result.Add(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return result
            .OrderByDescending(s => s.LastPlayed ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static SaveEntry? Read(string worldDir, string levelDat)
    {
        try
        {
            var root = NbtFile.Read(levelDat);
            var data = root["Data"];
            if (data is null) return null;

            var levelName = data["LevelName"]?.AsString();
            var gameType = data["GameType"]?.AsInt() ?? 0;
            var hardcore = data["hardcore"]?.AsBool() ?? false;
            var lastPlayedMs = data["LastPlayed"]?.AsLong() ?? 0;
            var version = data["Version"]?["Name"]?.AsString();

            DateTimeOffset? lastPlayed = lastPlayedMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastPlayedMs).ToLocalTime()
                : null;

            var icon = Path.Combine(worldDir, "icon.png");
            return new SaveEntry(
                Path.GetFileName(worldDir),
                string.IsNullOrWhiteSpace(levelName) ? Path.GetFileName(worldDir) : levelName!,
                GameModeText(gameType, hardcore),
                lastPlayed,
                DirectorySize(worldDir),
                hardcore,
                version,
                File.Exists(icon) ? icon : null);
        }
        catch (Exception)
        {
            // 单个世界损坏不该让整张列表消失
            return null;
        }
    }

    private static string GameModeText(int gameType, bool hardcore)
    {
        var mode = gameType switch
        {
            1 => "创造模式",
            2 => "冒险模式",
            3 => "旁观模式",
            _ => "生存模式",
        };
        return hardcore ? mode + " · 极限" : mode;
    }

    /// <summary>目录字节数。用作「这个存档多大」的展示与导出前的容量提示。</summary>
    public static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch (Exception) { /* 被占用的文件跳过 */ }
            }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 把世界打包成 zip（快照备份 / 导出）。
    /// 默认目标：实例目录下 <c>backups/&lt;世界&gt;-&lt;时间&gt;.zip</c>。
    /// 备份前不停止游戏是危险的（region 文件可能半写），调用方必须先确认进程已退出。
    /// </summary>
    public static string Backup(string clientName, string folder, string? targetZip = null)
    {
        var source = Path.Combine(ClientStore.SavesDir(clientName), folder);
        if (!Directory.Exists(source)) throw CeobeException.Of("存档不存在: {0}", folder);

        var target = targetZip ?? Path.Combine(
            ClientStore.Dir(clientName), "backups",
            $"{folder}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

        var dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir)) CeobeHome.Ensure(dir);
        try
        {
            ZipFile.CreateFromDirectory(source, target, CompressionLevel.Optimal, includeBaseDirectory: true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"备份存档失败: {e.Message}", e);
        }
    }

    /// <summary>把备份 zip 还原到 saves/ 下（重名时加 -restored 后缀，不覆盖现有世界）。</summary>
    public static string Restore(string clientName, string zipPath)
    {
        if (!File.Exists(zipPath)) throw CeobeException.Of("备份文件不存在: {0}", zipPath);
        var saves = ClientStore.SavesDir(clientName);
        var baseName = Path.GetFileNameWithoutExtension(zipPath);
        var target = Path.Combine(saves, baseName);
        var n = 1;
        while (Directory.Exists(target))
        {
            target = Path.Combine(saves, $"{baseName}-restored{n++}");
        }
        ZipUtil.Extract(zipPath, saves);
        // CreateFromDirectory(includeBaseDirectory) 会带上顶层目录，解压后名字应与 zip 同名
        return Directory.Exists(target) ? target : saves;
    }

    /// <summary>世界目录下的会话锁：存在 <c>session.lock</c> 且进程在跑时不可备份/删除。</summary>
    public static bool IsLocked(string clientName, string folder) =>
        File.Exists(Path.Combine(ClientStore.SavesDir(clientName), folder, "session.lock"));
}
