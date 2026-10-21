using System.Text.Json;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// Fabric（meta v2 + installer 子进程）。
/// <para>安装方式：<c>java -jar fabric-installer.jar server -dir &lt;d&gt; -mcversion &lt;v&gt; -loader &lt;l&gt;</c>；
/// 产物为 <c>fabric-server-launch.jar</c>（+ libraries/）。</para>
/// <para>语义差异（对齐 docs/specs/module-providers.md 的实测契约，非类注释里的旧命令）：
/// 不传 <c>-downloadMinecraft</c>——先由 ceobe 走镜像预拉原版 <c>server.jar</c>，
/// 安装器随即生成本地加载器，避免安装器直连 Mojang（Java 实现代码本就如此，仅 javadoc 滞后）。</para>
/// </summary>
public sealed class FabricProvider : IServerJarProvider
{
    private const string Meta = "https://meta.fabricmc.net/v2/versions";

    public ServerType Type => ServerType.FABRIC;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        using var doc = JsonUtil.Parse(
            await HttpUtil.GetStringAsync(Meta + "/game", ct).ConfigureAwait(false));
        var outList = new List<string>();
        foreach (var v in doc.RootElement.Items())
        {
            var version = v.Prop("version").AsText() ?? "";
            if (v.Prop("stable").AsBool() && VersionUtil.IsRelease(version)) outList.Add(version);
        }
        outList.Sort(VersionUtil.Descending);
        return outList;
    }

    public async Task<IReadOnlyList<string>> ListLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
    {
        using var doc = JsonUtil.Parse(
            await HttpUtil.GetStringAsync(Meta + "/loader/" + gameVersion, ct).ConfigureAwait(false));
        var outList = new List<string>();
        foreach (var v in doc.RootElement.Items())
        {
            if (v.Prop("loader").Prop("stable").AsBool())
            {
                outList.Add(v.Prop("loader").Prop("version").AsText() ?? "");
            }
        }
        return outList.Count > 0 ? outList : await LatestLoadersAsync(ct).ConfigureAwait(false);
    }

    /// <summary>全局 stable loader（字符串逆序，对齐 Java <c>Comparator.reverseOrder()</c>）。</summary>
    private static async Task<IReadOnlyList<string>> LatestLoadersAsync(CancellationToken ct)
    {
        using var doc = JsonUtil.Parse(
            await HttpUtil.GetStringAsync(Meta + "/loader", ct).ConfigureAwait(false));
        var outList = new List<string>();
        foreach (var v in doc.RootElement.Items())
        {
            if (v.Prop("stable").AsBool()) outList.Add(v.Prop("version").AsText() ?? "");
        }
        outList.Sort(StringComparer.Ordinal);
        outList.Reverse();
        return outList;
    }

    public async Task<InstalledServer> InstallAsync(
        string instanceDir, string gameVersion, string? loaderVersion,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        string loader;
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var latest = await LatestLoadersAsync(ct).ConfigureAwait(false);
            if (latest.Count == 0) throw new CeobeException("Fabric 无可用 stable loader");
            loader = latest[0];
        }
        else
        {
            loader = loaderVersion;
        }

        string installerUrl;
        using (var doc = JsonUtil.Parse(
                   await HttpUtil.GetStringAsync(Meta + "/installer", ct).ConfigureAwait(false)))
        {
            installerUrl = doc.RootElement.EnumerateArray().First().Prop("url").AsText() ?? "";
        }

        ProviderSupport.Report(progress, "下载 Fabric 安装器", 0, -1);
        var installer = await ProviderSupport.CachedJarAsync(
            "fabric-installer.jar", installerUrl, null, null, progress, ct).ConfigureAwait(false);

        // 预拉原版服务端（走镜像站），安装器随即生成本地加载器——不再由安装器自行从 Mojang 下载
        ProviderSupport.Report(progress, "准备原版服务端 " + gameVersion, 0, -1);
        await new MojangProvider().InstallAsync(instanceDir, gameVersion, null, progress, ct)
            .ConfigureAwait(false);

        ProviderSupport.Report(progress, "运行 Fabric 安装器", 0, -1);
        await ProviderSupport.RunCapturedAsync(
            "Fabric 安装器",
            ProviderSupport.JavaBinary(),
            new[]
            {
                "-jar", Path.GetFullPath(installer), "server",
                "-dir", Path.GetFullPath(instanceDir),
                "-mcversion", gameVersion,
                "-loader", loader,
            },
            instanceDir, ct).ConfigureAwait(false);

        var launch = Path.Combine(instanceDir, "fabric-server-launch.jar");
        if (!File.Exists(launch))
        {
            throw new CeobeException("Fabric 安装未产出 fabric-server-launch.jar，请检查安装器输出");
        }
        ProviderSupport.Report(progress, "安装完成", 1, 1);
        return new InstalledServer("fabric-server-launch.jar", gameVersion, loader);
    }
}
