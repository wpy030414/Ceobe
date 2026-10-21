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
            var inst = pr.GetValue(instance);
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
            var inst = pr.GetValue(instance);
            var proj = pr.GetValue(project);
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

            // 写回实例元数据（对齐 Java：同名文件旧记录被替换）
            var meta = status.Meta;
            meta.Mods.RemoveAll(m => m.FileName == entry.FileName);
            meta.Mods.Add(entry);
            WriteMeta(dir, meta);

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
            var inst = pr.GetValue(instance);
            var tgt = pr.GetValue(target);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var meta = status.Meta;
            var match = meta.Mods.FirstOrDefault(m =>
                m.FileName == tgt || m.Slug == tgt || m.ProjectId == tgt)
                ?? throw CeobeException.Of("实例中未找到模组: {0}", tgt);
            var dir = manager.Dir(inst);
            await new ModrinthClient().UninstallAsync(dir, match, ct).ConfigureAwait(false);
            meta.Mods.Remove(match);
            WriteMeta(dir, meta);
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
            var inst = pr.GetValue(instance);
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

    /// <summary>模组清单写回 instance.json（字段级兼容 Java，JsonUtil 统一 camelCase/null 省略）。</summary>
    private static void WriteMeta(string dir, InstanceMeta meta)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, "instance.json"), JsonUtil.Write(meta),
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("写入实例元数据失败: " + e.Message, e);
        }
    }
}
