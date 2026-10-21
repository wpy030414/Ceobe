using System.Security.Cryptography;
using System.Text;

namespace Ceobe.Core.Players;

/// <summary>
/// 离线模式玩家 UUID（<c>online-mode=false</c> 时服务端给玩家分配的固定 id）。
///
/// 算法：MD5("OfflinePlayer:" + name) 的 UTF-8 字节，按 RFC 4122 v3 置 version/variant 位。
/// 这与 Java <c>UUID.nameUUIDFromBytes</c> 逐位一致，且必须一致——白名单里的 UUID 若与
/// 服务端算出的不同，写入的文件会被服务端接受但**玩家仍然进不来**，属于最难查的那类静默失败。
/// 期望值由本机 JDK 25 实测生成（见 OfflinePlayerUuidTest 的 golden 表），非推演。
///
/// 实现上刻意**不用** <c>new Guid(byte[])</c>：.NET 的该构造器把前 4 字节按小端序解释
/// （Data1 = bytes[3..0]），而 UUID 语义是大端，直接喂 MD5 结果会得到错位的 UUID。
/// 这里统一走「十六进制字符串 + 固定分段」，从根上绕开字节序陷阱。
/// </summary>
public static class OfflinePlayerUuid
{
    /// <summary>服务端用的键前缀，逐字符固定（含冒号，无空格）。</summary>
    internal const string Prefix = "OfflinePlayer:";

    /// <summary>计算离线玩家 UUID（小写、带连字符的 RFC 4122 形式）。</summary>
    public static string Compute(string name)
    {
        name ??= string.Empty;
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(Prefix + name));

        // RFC 4122：byte[6] 高 4 位为版本号（3 = MD5 name-based），byte[8] 高 2 位为 variant（10 = RFC 4122）
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return Format(hex);
    }

    /// <summary>32 位十六进制 → 带连字符的 UUID 文本（8-4-4-4-12）。</summary>
    internal static string Format(string hex)
    {
        return string.Concat(
            hex[..8], "-",
            hex[8..12], "-",
            hex[12..16], "-",
            hex[16..20], "-",
            hex[20..32]);
    }

    /// <summary>
    /// 转成 MC 新版 JSON 里的四段有符号整数形式 <c>[msb_hi, msb_lo, lsb_hi, lsb_lo]</c>。
    /// 部分 1.16+ 的数据文件用这种形态；本仓库的 whitelist/ops 仍写字符串形式，此方法供读入兼容与后续扩展。
    /// </summary>
    public static int[] ToIntArray(string uuid)
    {
        var hex = uuid.Replace("-", "").ToLowerInvariant();
        if (hex.Length != 32) throw new Util.CeobeException("非法 UUID（应为 32 位十六进制）: " + uuid);

        var msb = Convert.ToUInt64(hex[..16], 16);
        var lsb = Convert.ToUInt64(hex[16..], 16);
        return [
            unchecked((int)(msb >> 32)),
            unchecked((int)msb),
            unchecked((int)(lsb >> 32)),
            unchecked((int)lsb),
        ];
    }

    /// <summary>由四段整数形式还原字符串 UUID（读入兼容用）。</summary>
    public static string FromIntArray(IReadOnlyList<int> parts)
    {
        if (parts.Count != 4) throw new Util.CeobeException("UUID 整数形式需要 4 段");
        ulong msb = ((ulong)(uint)parts[0] << 32) | (uint)parts[1];
        ulong lsb = ((ulong)(uint)parts[2] << 32) | (uint)parts[3];
        var hex = msb.ToString("x16") + lsb.ToString("x16");
        return Format(hex);
    }

    /// <summary>规范化：小写 + 补全连字符；非法输入返回 null（用于容错读文件）。</summary>
    public static string? Normalize(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        var s = uuid.Trim().ToLowerInvariant();
        if (s.Length == 32 && !s.Contains('-')) s = Format(s);
        return Guid.TryParse(s, out var g) ? g.ToString() : null;
    }
}
