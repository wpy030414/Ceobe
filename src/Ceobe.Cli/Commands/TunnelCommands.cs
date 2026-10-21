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
            var inst = pr.RequiredTrimmed(instance);
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
            // 建链（Pinggy 约 20-45s）需长超时——InstanceControl.TunnelUpReport 内部已用 LongTimeout。
            // 用类型化报告而非裸行：协议是竖线分隔，诊断文本可含任意字符，解析只能有一处。
            var report = InstanceControl.TunnelUpReport(dir, k.Id());
            var url = report.Url ?? "(未返回地址)";
            var expires = report.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "kind", k.Id(),
                    "url", url, "expiresAt", expires,
                    // 整改项 1(b) 的探测结论：verified 为 null 表示该通道类型不做 MC 探测
                    "verified", report.Verified, "diagnostic", report.Diagnostic));
            }
            else
            {
                Output.Info("通道已开启 [{0}]", k.ZhLabel());
                Output.Info("地址: {0}", url);
                if (expires.Length > 0) Output.Info("有效期至: {0}（到期前自动续期）", expires);
                // 整改项 1(b) 的价值全在这段：把「地址已给出」和「地址真能连」区分开。
                // 措辞按 Core 侧约定——false 不等于通道坏了，可能是防火墙/频率限制。
                if (report.Verified == true)
                {
                    Output.Info("可达性: 已实测 ✓（{0}）", Truncate(report.Diagnostic, 160) ?? "MC 应答正常");
                }
                else if (report.Verified == false)
                {
                    Output.Warn("可达性: 无法验证 —— {0}",
                        Truncate(report.Diagnostic, 200) ?? "探测未取得 MC status 响应");
                    Output.Info("这不代表通道一定坏了（可能有防火墙或频率限制），但如果朋友连不上，先查这条。");
                }
                else if (report.Diagnostic is not null)
                {
                    Output.Info("{0}", Truncate(report.Diagnostic, 200)!);
                }
                UsageHint(k);
            }
            return 0;
        });
        return cmd;
    }

    /// <summary>诊断文本可能很长（探测过程有多条原因），按显示宽度截断避免刷屏。</summary>
    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var oneLine = s.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
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
            var inst = pr.RequiredTrimmed(instance);
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
            var inst = pr.RequiredTrimmed(instance);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(inst);
            var dir = manager.Dir(inst);
            var rows = new List<string[]>();
            var reports = new List<(string Kind, TunnelReport Report)>();
            if (InstanceControl.IsAvailable(dir))
            {
                // 类型化报告：不手拆竖线（诊断文本本身可能含分隔符形态的字符）
                reports = InstanceControl.TunnelStatusReports(dir);
                foreach (var (kind, report) in reports)
                {
                    rows.Add([
                        Label(kind),
                        report.Url ?? "-",
                        report.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "不过期",
                        Verdict(report.Verified),
                    ]);
                }
            }
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst,
                    "running", status.State.IsLive(), "localPort", status.Meta.Port,
                    "tunnels", reports.Select(r => Output.Map(
                        "kind", r.Kind, "url", r.Report.Url,
                        "expiresAt", r.Report.ExpiresAt?.ToString("o"),
                        "verified", r.Report.Verified,
                        "diagnostic", r.Report.Diagnostic)).ToList()));
                return 0;
            }
            Output.Info("实例 {0}（{1}）本机端口 {2}", inst, status.State.ZhLabel(), status.Meta.Port);
            if (rows.Count == 0)
            {
                Output.Info("当前无活动通道。开启: ceobe tunnel up {0}", inst);
                return 0;
            }
            Output.Table(["通道", "公网地址", "有效期至", "可达性"], rows);
            // 整改项 1(b)：把每条通道的探测诊断摊开，而不是只给一个「已开启」
            foreach (var (kind, report) in reports)
            {
                if (report.Diagnostic is null) continue;
                Output.Info("  · {0}: {1}", Label(kind), Truncate(report.Diagnostic, 200)!);
            }
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

    /// <summary>
    /// 探测结论的表格文案。三态必须分开写：
    /// 「未验证」不能显示成「失败」，否则 Cloudflare 这类不做 MC 探测的通道会被误读成坏了。
    /// </summary>
    private static string Verdict(bool? verified) => verified switch
    {
        true => "已实测可达 ✓",
        false => "无法验证（见下）",
        null => "未探测",
    };
}
