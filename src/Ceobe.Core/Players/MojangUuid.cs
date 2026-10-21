using System.Globalization;
using System.Text;
using Ceobe.Core.Util;

namespace Ceobe.Core.Players;

/// <summary>
/// 正版（online-mode=true）玩家 UUID 解析：查 Mojang 官方 profile API。
///
/// 为什么必须查而不能留空：whitelist.json / ops.json 里服务端认的是 UUID 而非名字。
/// 正版模式下 UUID 由 Mojang 账号系统决定（与玩家名的当前大小写无关，且改名后 UUID 不变），
/// 本地无法推算。写空串或写离线算法的结果，服务端会**接受文件但玩家永远匹配不上**——
/// 这是最难查的静默失败，故查不到就明确报错，绝不写占位值。
/// </summary>
public static class MojangUuid
{
    /// <summary>
    /// 按玩家名查正版 UUID（带连字符的小写形态，与 whitelist.json 一致）。
    /// 名字不存在 / 网络失败 / 被限流 → 返回 null，由调用方决定提示文案。
    /// </summary>
    /// <remarks>
    /// 端点 <c>https://api.mojang.com/users/profiles/minecraft/{name}</c>，
    /// 响应形如 <c>{"id":"853c80ef3c3749fdaa49938b674adae6","name":"jeb_"}</c>，
    /// id 是无连字符的 32 位十六进制。实测 2026-10-21 可用（HTTP 200）。
    /// 该 API 无镜像且未纳入 Mirrors（属账号服务而非下载源），直连。
    /// </remarks>
    public static async Task<string?> ResolveAsync(string playerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return null;
        var name = playerName.Trim();
        // Mojang 名字规则：3-16 位字母数字下划线；不合规直接不查，省一次网络往返
        if (name.Length is < 3 or > 16) return null;

        try
        {
            var url = "https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(name);
            var body = await HttpUtil.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonUtil.Parse(body);
            if (!doc.RootElement.TryGetProperty("id", out var idEl)) return null;
            var raw = idEl.GetString();
            return FormatUuid(raw);
        }
        catch (CeobeException)
        {
            // 404（名字不存在）/ 429（限流）/ 网络失败都归一到 null，调用方给提示
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>32 位无连字符十六进制 → 8-4-4-4-12 小写形态；非法输入返回 null。</summary>
    internal static string? FormatUuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var hex = raw.Trim().Replace("-", "").ToLowerInvariant();
        if (hex.Length != 32) return null;
        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        return string.Concat(
            hex[..8], "-", hex[8..12], "-", hex[12..16], "-", hex[16..20], "-", hex[20..32]);
    }
}
