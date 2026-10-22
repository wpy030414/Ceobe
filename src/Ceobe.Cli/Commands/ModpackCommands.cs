using System.CommandLine;
using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// Modrinth 整合包（.mrpack）命令族。逐条对齐 Java 版 <c>ceobe-cli/ModpackCommands</c>。
/// </summary>
public static class ModpackCommands
{
    public static Command Root()
    {
        var cmd = new Command("modpack", "Modrinth 整合包（.mrpack）搜索与安装")
        {
            Search(), Info(), Install(), New(),
        };
        cmd.SetAction(_ =>
        {
            Output.Info("用法: Ceobe modpack <search|info|install|new> …");
            return 0;
        });
        return cmd;
    }

    // ========== search ==========

    private static Command Search()
    {
        var query = new Argument<string?>("关键词") { Arity = ArgumentArity.ZeroOrOne };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 15 };
        var version = new Option<string?>("--version") { Description = "按 MC 版本过滤" };
        var loader = new Option<string?>("--loader") { Description = "按加载器过滤: fabric | neoforge" };

        var cmd = new Command("search", "搜索整合包") { query, limit, version, loader };
        cmd.SetAction(async (pr, ct) =>
        {
            var q = pr.GetValue(query) ?? "";
            var hits = await new ModrinthClient()
                .SearchPacks(q, pr.GetValue(version), pr.GetValue(loader), pr.GetValue(limit), ct)
                .ConfigureAwait(false);
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("query", q, "hits",
                    hits.Select(h => Output.Map("slug", h.Slug, "title", h.Title, "author", h.Author,
                        "downloads", h.Downloads, "categories", h.Categories)).ToList()));
                return 0;
            }
            if (hits.Count == 0)
            {
                Output.Info("没有匹配的整合包");
                return 0;
            }
            var rows = hits.Select(h => new[]
            {
                h.Slug, h.Title, h.Author, h.Downloads.ToString(),
                string.Join(",", h.Categories.Take(3)),
            }).ToList();
            Output.Table(["标识", "名称", "作者", "下载量", "分类"], rows);
            Output.Info("一键创建: Ceobe modpack new <名称> <标识>");
            return 0;
        });
        return cmd;
    }

    // ========== info ==========

    private static Command Info()
    {
        var pack = new Argument<string>("标识");
        var version = new Option<string?>("--version") { Description = "限定 MC 版本" };
        var loader = new Option<string?>("--loader") { Description = "限定加载器" };

        var cmd = new Command("info", "查看整合包的版本 / 加载器 / 文件") { pack, version, loader };
        cmd.SetAction(async (pr, ct) =>
        {
            var pv = await new ModrinthClient()
                .ResolvePack(pr.RequiredTrimmed(pack), pr.GetValue(version), pr.GetValue(loader), ct)
                .ConfigureAwait(false);
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("slug", pv.Slug, "name", pv.Name,
                    "version", pv.VersionNumber, "gameVersions", pv.GameVersions,
                    "loaders", pv.Loaders, "url", pv.Url, "size", pv.Size));
                return 0;
            }
            Output.Info("整合包: {0}", pv.Name);
            Output.Info("版本: {0}（MC {1} / {2}）", pv.VersionNumber,
                pv.GameVersions.Count == 0 ? "?" : pv.GameVersions[0],
                pv.Loaders.Count == 0 ? "?" : string.Join(",", pv.Loaders));
            Output.Info("文件: {0}（{1} bytes）", pv.Url, pv.Size);
            return 0;
        });
        return cmd;
    }

    // ========== install ==========

    private static Command Install()
    {
        var instance = new Argument<string>("实例");
        var pack = new Argument<string>("标识");
        var version = new Option<string?>("--version") { Description = "限定整合包 MC 版本" };

        var cmd = new Command("install", "把整合包安装到已有实例（需加载器与版本一致）")
        { instance, pack, version };
        cmd.SetAction(async (pr, ct) =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            if (status.State.IsLive())
            {
                throw CeobeException.Of("实例 {0} 正在运行，请先停止再装整合包", inst);
            }
            var zip = await DownloadPackAsync(pr.RequiredTrimmed(pack), pr.GetValue(version),
                status.Meta.ServerType.LoaderId(), ct).ConfigureAwait(false);
            var idx = MrpackInstaller.ReadIndex(zip);
            CheckCompat(status, idx);

            var progress = CliParse.ProgressBar();
            var r = await MrpackInstaller.InstallAsync(manager.Dir(inst), zip, progress, ct)
                .ConfigureAwait(false);
            CliParse.ProgressDone();
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "pack", r.Name,
                    "version", r.VersionId, "files", r.Files, "skipped", r.Skipped,
                    "overrides", r.Overrides));
            }
            else
            {
                Output.Info("已安装整合包「{0}」→ 实例 {1}（{2} 个文件，跳过 {3}，覆盖 {4}）",
                    r.Name, inst, r.Files, r.Skipped, r.Overrides);
            }
            return 0;
        });
        return cmd;
    }

    // ========== new ==========

    private static Command New()
    {
        var name = new Argument<string>("名称");
        var pack = new Argument<string>("标识");
        var mem = new Option<string>("--mem") { DefaultValueFactory = _ => "4G", Description = "内存上限，如 4G" };
        var port = new Option<string>("--port") { DefaultValueFactory = _ => "25565", Description = "端口或 auto" };
        var jvm = new Option<string>("--jvm") { DefaultValueFactory = _ => "", Description = "附加 JVM 参数" };
        var javaHome = new Option<string?>("--java-home") { Description = "指定 JRE 目录" };

        var cmd = new Command("new", "按整合包自动创建实例并安装（一键）")
        { name, pack, mem, port, jvm, javaHome };
        cmd.SetAction(async (pr, ct) =>
        {
            var n = pr.RequiredTrimmed(name);
            var zip = await DownloadPackAsync(pr.RequiredTrimmed(pack), null, null, ct).ConfigureAwait(false);
            var idx = MrpackInstaller.ReadIndex(zip);
            if (idx.McVersion is null)
            {
                throw new CeobeException("整合包未声明 MC 版本，无法自动创建");
            }
            var type = ServerTypeInfo.FromLoaderId(idx.Loader);

            var manager = CoreWiring.NewManager();
            var spec = new InstanceSpec(
                n, type, idx.McVersion, idx.LoaderVersion,
                CliParse.Memory(pr.GetValue(mem)!), pr.GetValue(jvm),
                CliParse.ResolvePort(pr.GetValue(port), manager), pr.GetValue(javaHome));

            var progress = CliParse.ProgressBar();
            await manager.CreateAsync(spec, progress, ct).ConfigureAwait(false);
            CliParse.ProgressDone();
            var r = await MrpackInstaller.InstallAsync(manager.Dir(n), zip, progress, ct)
                .ConfigureAwait(false);
            CliParse.ProgressDone();
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", n, "type", type.ToString(),
                    "mcVersion", idx.McVersion, "loader", idx.Loader,
                    "loaderVersion", idx.LoaderVersion, "pack", r.Name, "files", r.Files));
            }
            else
            {
                Output.Info("已创建实例 {0}（{1} {2} / {3}），并安装整合包「{4}」（{5} 个文件）",
                    n, type, idx.McVersion, idx.Loader, r.Name, r.Files);
                Output.Info("启动: Ceobe start {0}", n);
            }
            return 0;
        });
        return cmd;
    }

    // —— 辅助 ——

    /// <summary>下载 .mrpack 到缓存目录（cache/packs），返回本地路径；已存在则复用（对齐 Java fetch）。</summary>
    private static async Task<string> DownloadPackAsync(string pack, string? version, string? loader,
        CancellationToken ct)
    {
        var pv = await new ModrinthClient().ResolvePack(pack, version, loader, ct).ConfigureAwait(false);
        return await MrpackInstaller.FetchAsync(pv, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 装到已有实例前的兼容性闸门：MC 版本必须一致；加载器必须对得上。
    /// 逐条对齐 Java <c>checkCompat</c>（含 want==null 即「该类型不支持整合包」的分支）。
    /// </summary>
    private static void CheckCompat(InstanceStatus status, MrpackInstaller.Index idx)
    {
        if (idx.McVersion is not null && idx.McVersion != status.Meta.GameVersion)
        {
            throw CeobeException.Of("实例是 MC {0}，整合包要求 MC {1}；请创建匹配的实例",
                status.Meta.GameVersion, idx.McVersion);
        }
        var want = status.Meta.ServerType.LoaderId();
        if (idx.Loader is not null && want is not null && idx.Loader != want)
        {
            throw CeobeException.Of("实例加载器是 {0}，整合包要求 {1}", want, idx.Loader);
        }
        if (want is null)
        {
            throw CeobeException.Of("{0} 不支持装整合包（加载器不对）", status.Meta.ServerType.ZhLabel());
        }
    }
}
