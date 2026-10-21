using System.Text.Json;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// PaperMC Fill v3（paper / folia）。
/// <para>契约：<c>/v3/projects/{p}/versions/{v}/builds/latest</c> →
/// <c>downloads["server:default"].{url,checksums.sha256,size}</c>。</para>
/// <para>注意：旧的 <c>api.papermc.io/v2</c> 已 410 Gone，不得回退使用。</para>
/// </summary>
public sealed class PaperProvider : IServerJarProvider
{
    private const string Base = "https://fill.papermc.io/v3/projects";

    private readonly string _project;
    private readonly ServerType _type;

    public PaperProvider() : this("paper", ServerType.PAPER) { }

    /// <summary>folia 等 Fill v3 项目复用本实现（对齐 Java 双参构造器）。</summary>
    public PaperProvider(string project, ServerType type)
    {
        _project = project;
        _type = type;
    }

    public ServerType Type => _type;

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        using var doc = JsonUtil.Parse(
            await HttpUtil.GetStringAsync(Base + "/" + _project, ct).ConfigureAwait(false));
        var outList = new List<string>();
        foreach (var e in doc.RootElement.Prop("versions").Properties())
        {
            foreach (var v in e.Value.Items())
            {
                var id = v.AsText() ?? "";
                if (VersionUtil.IsRelease(id)) outList.Add(id);
            }
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
            doc = JsonUtil.Parse(await HttpUtil
                .GetStringAsync($"{Base}/{_project}/versions/{gameVersion}/builds/latest", ct)
                .ConfigureAwait(false));
        }
        catch (CeobeException e)
        {
            throw new CeobeException(
                $"{_project} 暂不支持版本 {gameVersion}（可换版本或改用其他服务端类型）", e);
        }

        using (doc)
        {
            // key 含冒号（server:default），System.Text.Json 属性名允许，无需 DTO 转义
            var download = doc.RootElement.Prop("downloads").Prop("server:default");
            if (download.IsMissing())
            {
                throw new CeobeException(_project + " 未返回服务端下载信息，可能无该版本构建");
            }
            var url = download.Prop("url").AsText() ?? "";
            var sha256 = download.Prop("checksums").Prop("sha256").AsText(null);
            var size = download.Prop("size").AsLong(-1);

            ProviderSupport.Report(progress, $"下载 {_project} {gameVersion}", 0, size);
            var cached = await ProviderSupport.CachedJarAsync(
                $"{_project}-{gameVersion}.jar", url, null, sha256, progress, ct).ConfigureAwait(false);
            ProviderSupport.Copy(cached, Path.Combine(instanceDir, "server.jar"));
            ProviderSupport.Report(progress, "安装完成", size, size);
            return new InstalledServer("server.jar", gameVersion, null);
        }
    }
}
