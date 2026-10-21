using System.CommandLine;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// 穿透命令族。
/// <para>通道经实例控制通道下发（由监督进程持有），因此实例必须处于运行中。</para>
/// <para>逐条对齐 Java 版 <c>ceobe-cli/TunnelCommands</c>；C# 版新增 tailscale 通道，
/// 相应提示文案有意的扩展见各分支注释。</para>
/// </summary>
public static class TunnelCommands
{
    public static Command Root()
    {
        var cmd = new Command("tunnel", "内网穿透通道管理") { Up(), Down(), Status() };
        cmd.SetAction(_ =>
        {
            // 与 Java 版文案一致；新增 tailscale 是本版真实能力，选项说明一并列出
            Output.Info("用法: ceobe tunnel <up|down|status> <实例> [--kind pinggy|cloudflare|lan|tailscale]");
            return 0;
        });
        return cmd;
    }

    // ========== up ==========

    private static Command Up()
    {
        var instance = new Argument<string>("实例");
        var kind = new Option<string>("--kind")
        {
            DefaultValueFactory = _ => "pinggy",
            Description = "通道类型: pinggy | cloudflare | lan | tailscale（默认 pinggy）",
        };

        var cmd = new Command("up", "开启通道（pinggy=玩家连接，cloudflare=Web 控制台）") { instance, kind };
        cmd.SetAction(pr =>
        {
            var inst = pr.GetValue(instance);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            if (!status.State.IsLive())
            {
                throw CeobeException.Of("实例 {0} 未在运行，请先 `ceobe start -d {0}`", inst);
            }
            var dir = manager.Dir(inst);
            if (!InstanceControl.IsAvailable(dir))
            {
                throw CeobeException.Of("实例 {0} 无控制通道（需以受控方式启动）", inst);
            }
            var k = TunnelKindInfo.FromId(pr.GetValue(kind)!);
            // 建链（Pinggy 约 20-45s）需长超时——InstanceControl.TunnelUp 内部已用 LongTimeout
            var result = InstanceControl.TunnelUp(dir, k.Id());
            var url = result.Count == 0 ? "(未返回地址)" : result[0];
            var expires = result.Count > 1 ? result[1] : "";
            if (expires == "-") expires = "";

            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "kind", k.Id(),
                    "url", url, "expiresAt", expires));
            }
            else
            {
                Output.Info("通道已开启 [{0}]", k.ZhLabel());
                Output.Info("地址: {0}", url);
                if (expires.Length > 0) Output.Info("有效期至: {0}（到期前自动续期）", expires);
                UsageHint(k);
                // 整改项 1 的 Diagnostic/Verified 无法经控制通道传回监督进程外的 CLI：
                // 见交付报告「拿不到的数据」。这里不编造，仅在 tailscale/lan 分支给出可自查的下一步。
            }
            return 0;
        });
        return cmd;
    }

    /// <summary>各通道的用法提示。pinggy/cloudflare 文案逐字对齐 Java；lan/tailscale 为 C# 版扩展。</summary>
    private static void UsageHint(TunnelKind k)
    {
        switch (k)
        {
            case TunnelKind.PINGGY:
                Output.Info("把该地址发给朋友，在「多人游戏 → 直接连接」中填入即可。");
                break;
            case TunnelKind.CLOUDFLARE:
                Output.Info("浏览器打开该地址即可远程查看服务器状态（只读）。");
                break;
            case TunnelKind.LAN:
                Output.Info("同一局域网内的朋友在「多人游戏 → 直接连接」中填入该地址即可（无需任何工具）。");
                break;
            case TunnelKind.TAILSCALE:
                Output.Info("朋友需先安装 tailscale 并加入同一 tailnet，再在「多人游戏 → 直接连接」中填入该地址；"
                            + "地址长期稳定，适合固定联机伙伴（推荐存进服务器列表）。");
                break;
        }
    }

    // ========== down ==========

    private static Command Down()
    {
        var instance = new Argument<string>("实例");
        var kind = new Option<string>("--kind")
        {
            DefaultValueFactory = _ => "all",
            Description = "通道类型（默认全部关闭）",
        };

        var cmd = new Command("down", "关闭通道") { instance, kind };
        cmd.SetAction(pr =>
        {
            var inst = pr.GetValue(instance);
            var k = pr.GetValue(kind);
            var manager = CoreWiring.NewManager();
            manager.Require(inst);
            var dir = manager.Dir(inst);
            if (!InstanceControl.IsAvailable(dir))
            {
                throw CeobeException.Of("实例 {0} 无控制通道", inst);
            }
            if (string.Equals(k, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kindValue in Enum.GetValues<TunnelKind>())
                {
                    try
                    {
                        InstanceControl.Call(dir, "TUNNEL DOWN " + kindValue.Id());
                    }
                    catch (CeobeException)
                    {
                        // 对齐 Java：单个通道关闭失败不影响其余（可能本来就没开）
                    }
                }
            }
            else
            {
                InstanceControl.TunnelDown(dir, TunnelKindInfo.FromId(k).Id());
            }
            Output.Info("通道已关闭: {0}", inst);
            return 0;
        });
        return cmd;
    }

    // ========== status ==========

    private static Command Status()
    {
        var instance = new Argument<string>("实例");
        var cmd = new Command("status", "查看通道状态与地址") { instance };
        cmd.SetAction(pr =>
        {
            var inst = pr.GetValue(instance);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var dir = manager.Dir(inst);
            var rows = new List<string[]>();
            if (InstanceControl.IsAvailable(dir))
            {
                foreach (var line in InstanceControl.TunnelStatus(dir))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 2)
                    {
                        rows.Add([Label(parts[0]), parts[1], parts.Length > 2 ? parts[2] : ""]);
                    }
                }
            }
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst,
                    "running", status.State.IsLive(), "localPort", status.Meta.Port,
                    "tunnels", rows.Select(r => Output.Map(
                        "kind", r[0], "url", r[1], "expiresAt", r[2])).ToList()));
                return 0;
            }
            Output.Info("实例 {0}（{1}）本机端口 {2}", inst, status.State.ZhLabel(), status.Meta.Port);
            if (rows.Count == 0)
            {
                Output.Info("当前无活动通道。开启: ceobe tunnel up {0}", inst);
                return 0;
            }
            Output.Table(["通道", "公网地址", "有效期至"], rows);
            return 0;
        });
        return cmd;
    }

    private static string Label(string id)
    {
        try
        {
            return TunnelKindInfo.FromId(id).ZhLabel();
        }
        catch (CeobeException)
        {
            return id;
        }
    }
}
