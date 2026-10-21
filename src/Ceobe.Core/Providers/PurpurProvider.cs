using System.Text.Json;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// Purpur 官方 API。
/// <para>契约：<c>/v2/purpur</c> 版本列表 → <c>/v2/purpur/{v}</c> builds.latest
/// → <c>/v2/purpur/{v}/{build}/download</c>。</para>
/// </summary>
public sealed class PurpurProvider : IServerJarProvider
{
    private const string Base = "https://api.purpurmc.org/v2/purpur";

    public ServerType Type => ServerType.PURPUR;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        using var doc = JsonUtil.Parse(
            await HttpUtil.GetStringAsync(Base, ct).ConfigureAwait(false));
        var outList = new List<string>();
        foreach (var v in doc.RootElement.Prop("versions").Items())
        {
            var id = v.AsText() ?? "";
            if (VersionUtil.IsRelease(id)) outList.Add(id);
        }
        outList.Sort(VersionUtil.Descending);
        return outList;
    }

    public async Task<InstalledServer> InstallAsync(
        string instanceDir, string gameVersion, string? loaderVersion,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = JsonUtil.Parse(
                await HttpUtil.GetStringAsync(Base + "/" + gameVersion, ct).ConfigureAwait(false));
        }
        catch (CeobeException)
        {
            // 对齐 Java：原异常作 cause 丢失，重抛为「无版本」；错误信息保持可操作
            throw CeobeException.Of("Purpur 无版本 {0}", gameVersion);
        }

        using (doc)
        {
            var build = doc.RootElement.Prop("builds").Prop("latest").AsText(null);
            if (string.IsNullOrWhiteSpace(build))
            {
                throw CeobeException.Of("Purpur {0} 尚无构建", gameVersion);
            }
            var url = $"{Base}/{gameVersion}/{build}/download";
            ProviderSupport.Report(progress, $"下载 Purpur {gameVersion} build {build}", 0, -1);
            var cached = await ProviderSupport.CachedJarAsync(
                $"purpur-{gameVersion}-{build}.jar", url, null, null, progress, ct).ConfigureAwait(false);
            ProviderSupport.Copy(cached, Path.Combine(instanceDir, "server.jar"));
            ProviderSupport.Report(progress, "安装完成", 1, 1);
            return new InstalledServer("server.jar", gameVersion, null);
        }
    }
}
