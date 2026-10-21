namespace Ceobe.Core.Util;

/// <summary>
/// 下载镜像映射：把上游 URL 改写到国内镜像站，显著加速（AGENTS「下载一律优先镜像」/ ADR-012）。
///
/// 已实测覆盖：
/// <list type="bullet">
///   <item>BMCLAPI（bmclapi2.bangbang93.com）：Mojang piston-meta/piston-data（同路径换主机）、Fabric meta（<c>/fabric-meta</c> 前缀）。</item>
///   <item>mcimirror（mod.mcimirror.top）：Modrinth CDN（同路径换主机）。</item>
/// </list>
/// 无镜像的源（PaperMC Fill、Purpur API、maven.neoforged.net、maven.fabricmc.net、api.modrinth.com）保持直连。
///
/// 环境变量 <c>CEOBE_MIRROR=off</c> 关闭镜像；<c>CEOBE_MIRROR_BMCLAPI</c> / <c>CEOBE_MIRROR_MODRINTH</c> 覆盖镜像基址。
/// （Java 版用系统属性 <c>-Dceobe.mirror</c>，C# 进程无 JVM 属性体系，等价改走环境变量。）
/// </summary>
public static class Mirrors
{
    private const string DefaultBmclapi = "https://bmclapi2.bangbang93.com";
    private const string DefaultModrinth = "https://mod.mcimirror.top";

    private static readonly string Bmclapi =
        Environment.GetEnvironmentVariable("CEOBE_MIRROR_BMCLAPI") ?? DefaultBmclapi;
    private static readonly string ModrinthCdn =
        Environment.GetEnvironmentVariable("CEOBE_MIRROR_MODRINTH") ?? DefaultModrinth;

    private static readonly bool Enabled =
        !string.Equals(Environment.GetEnvironmentVariable("CEOBE_MIRROR"), "off",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>返回该 URL 的镜像地址；无镜像或已禁用时返回 null（调用方回退上游）。</summary>
    public static string? ForUrl(string? url)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var u = new Uri(url, UriKind.Absolute);
            var host = (u.Host ?? string.Empty).ToLowerInvariant();
            var path = u.AbsolutePath;
            var suffix = string.IsNullOrEmpty(u.Query) ? string.Empty : u.Query;
            return host switch
            {
                "piston-meta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com"
                    => Bmclapi + path + suffix,
                "meta.fabricmc.net" => Bmclapi + "/fabric-meta" + path + suffix,
                "cdn.modrinth.com" => ModrinthCdn + path + suffix,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsEnabled => Enabled;

    /// <summary>Temurin JDK 走清华镜像（ADR-011），返回镜像地址；基址非官方时原样返回。</summary>
    public static string? ForTemurin(string url)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url)) return null;
        const string upstream = "https://github.com/adoptium/temurin";
        if (!url.StartsWith(upstream, StringComparison.OrdinalIgnoreCase)) return null;
        const string tsinghua = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium";
        var tail = url[upstream.Length..];
        // 上游 .../temurin25-binaries/releases/download/jdk-25.0.1%2B8/xxx.tar.gz
        // 镜像 .../Adoptium/25/jdk/x64/windows/xxx.zip  结构不同，交由 JdkInstaller 组装。
        return tail.Contains("releases") ? tsinghua : null;
    }
}
