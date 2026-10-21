using System.CommandLine;
using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// Modrinth 模组命令族。逐条对齐 Java 版 <c>ceobe-cli/ModCommands</c>。
/// 与 Java 相同：模组清单（instance.json 的 mods[]）由 CLI 侧写回——Core 的 Install 只负责下载。
/// </summary>
public static class ModCommands
{
    public static Command Root()
    {
        var cmd = new Command("mod", "Modrinth 模组搜索与安装")
        {
            Search(), Install(), Rm(), Ls(),
        };
        cmd.SetAction(_ =>
        {
            Output.Info("用法: ceobe mod <search|install|ls|rm> …");
            return 0;
        });
        return cmd;
    }

    // ========== search ==========

    private static Command Search()
    {
        var instance = new Argument<string>("实例");
        var query = new Argument<string?>("关键词") { Arity = ArgumentArity.ZeroOrOne };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 15 };

        var cmd = new Command("search", "搜索适配该实例的模组") { instance, query, limit };
        cmd.SetAction(async (pr, ct) =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var q = pr.GetValue(query) ?? "";
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var type = status.Meta.ServerType;
            if (!type.HasModsDir())
            {
                throw CeobeException.Of("{0} 不支持 mods 目录（无模组加载器）", type.ZhLabel());
            }
            var hits = await new ModrinthClient()
                .Search(q, status.Meta.GameVersion, type, pr.GetValue(limit), ct)
                .ConfigureAwait(false);
            if (Output.Json)
            {
                Output.JsonValue(Output.Map(
                    "query", q, "gameVersion", status.Meta.GameVersion, "loader", type.ToString(),
                    "hits", hits.Select(h => Output.Map(
                        "projectId", h.ProjectId, "slug", h.Slug, "title", h.Title,
                        "description", h.Description, "author", h.Author,
                        "downloads", h.Downloads, "categories", h.Categories)).ToList()));
                return 0;
            }
            if (hits.Count == 0)
            {
                Output.Info("没有找到适配 {0} + {1} 的模组",
                    status.Meta.GameVersion, type.ToString().ToLowerInvariant());
                return 0;
            }
            var rows = hits.Select(h => new[]
            {
                h.Slug, h.Title, h.Author, h.Downloads.ToString(),
                string.Join(",", h.Categories.Take(3)),
            }).ToList();
            Output.Table(["标识", "名称", "作者", "下载量", "分类"], rows);
            Output.Info("安装: ceobe mod install {0} <标识>", inst);
            return 0;
        });
        return cmd;
    }

    // ========== install ==========

    private static Command Install()
    {
        var instance = new Argument<string>("实例");
        var project = new Argument<string>("模组标识");
        var cmd = new Command("install", "安装模组（自动选适配版本）") { instance, project };
        cmd.Aliases.Add("add");
        cmd.SetAction(async (pr, ct) =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var proj = pr.RequiredTrimmed(project);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var type = status.Meta.ServerType;
            if (!type.HasModsDir())
            {
                throw CeobeException.Of("{0} 不支持 mods 目录（无模组加载器）", type.ZhLabel());
            }
            var dir = manager.Dir(inst);
            var progress = CliParse.ProgressBar();
            var entry = await new ModrinthClient()
                .Install(dir, type, proj, status.Meta.GameVersion, progress, ct)
                .ConfigureAwait(false);
            CliParse.ProgressDone();

            // 写回实例元数据：走 Core 的 AppendMods（同名文件替换旧记录）。
            // 不再在此处手工 RemoveAll+Add+WriteMeta——整改项 4 的 GUI 一键导入也要写 mods 清单，
            // 两处各写一份必然漂移（典型症状：CLI 装的 mod 在 GUI 列表里少一条）。
            manager.AppendMods(inst, [entry]);

            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "file", entry.FileName,
                    "version", entry.VersionNumber, "envServer", entry.EnvServer,
                    "envClient", entry.EnvClient));
            }
            else
            {
                Output.Info("已安装 {0} → mods/{1}（服务端侧: {2}）",
                    entry.Title, entry.FileName, entry.EnvServer);
            }
            return 0;
        });
        return cmd;
    }

    // ========== rm ==========

    private static Command Rm()
    {
        var instance = new Argument<string>("实例");
        var target = new Argument<string>("标识|文件名");
        var cmd = new Command("rm", "卸载模组（按文件名或标识）") { instance, target };
        cmd.Aliases.Add("uninstall");
        cmd.Aliases.Add("remove");
        cmd.SetAction(async (pr, ct) =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var tgt = pr.RequiredTrimmed(target);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var meta = status.Meta;
            var match = meta.Mods.FirstOrDefault(m =>
                m.FileName == tgt || m.Slug == tgt || m.ProjectId == tgt)
                ?? throw CeobeException.Of("实例中未找到模组: {0}", tgt);
            var dir = manager.Dir(inst);
            await new ModrinthClient().UninstallAsync(dir, match, ct).ConfigureAwait(false);
            // 走 Core 的 RemoveMods，与 GUI 卸载共用同一套写回逻辑
            manager.RemoveMods(inst, m => m.FileName == match.FileName);
            Output.Info("已卸载 {0}（{1}）", match.Title, match.FileName);
            return 0;
        });
        return cmd;
    }

    // ========== ls ==========

    private static Command Ls()
    {
        var instance = new Argument<string>("实例");
        var cmd = new Command("ls", "列出已安装模组") { instance };
        cmd.Aliases.Add("list");
        cmd.SetAction(pr =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var dir = manager.Dir(inst);
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "mods",
                    status.Meta.Mods.Select(m => Output.Map(
                        "file", m.FileName, "title", m.Title, "version", m.VersionNumber,
                        "envServer", m.EnvServer, "envClient", m.EnvClient, "sha1", m.Sha1)).ToList()));
                return 0;
            }
            var rows = status.Meta.Mods.Select(m => new[]
            {
                m.FileName, m.Title, m.VersionNumber, m.EnvServer,
                File.Exists(Path.Combine(dir, "mods", m.FileName)) ? "是" : "缺失",
            }).ToList();
            if (rows.Count == 0)
            {
                Output.Info("实例 {0} 尚未安装模组", inst);
                return 0;
            }
            Output.Table(["文件", "名称", "版本", "服务端侧", "文件存在"], rows);
            return 0;
        });
        return cmd;
    }
}
