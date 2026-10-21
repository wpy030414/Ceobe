using System.CommandLine;
using Ceobe.Core.Jdk;
using Ceobe.Core.Providers;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Ceobe.Core.WebConsole;

namespace Ceobe.Cli.Commands;

/// <summary>
/// 版本查询 / 环境自检 / 独立 Web 控制台。
/// 逐条对齐 Java 版 <c>ceobe-cli/MiscCommands</c>（命令名、选项、文案不变）。
/// </summary>
public static class MiscCommands
{
    // ========== versions ==========

    public static Command Versions()
    {
        var type = new Option<string>("--type")
        {
            DefaultValueFactory = _ => "paper",
            Description = "服务端类型: vanilla | paper | fabric | purpur",
        };
        var all = new Option<bool>("--all") { Description = "显示全部版本（默认每组显示最新 6 个）" };

        var cmd = new Command("versions", "列出服务端可选版本（按家族归组）") { type, all };
        cmd.SetAction(async (pr, ct) =>
        {
            var serverType = CliParse.Type(pr.GetValue(type)!);
            var versions = await ProviderRegistry.Of(serverType).ListVersionsAsync(ct).ConfigureAwait(false);
            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var v in versions)
            {
                if (!grouped.TryGetValue(VersionUtil.Family(v), out var list))
                {
                    list = [];
                    grouped[VersionUtil.Family(v)] = list;
                }
                list.Add(v);
            }
            if (Output.Json)
            {
                Output.JsonValue(Output.Map(
                    "type", serverType.ToString(),
                    "latest", versions.Count == 0 ? null : versions[0],
                    "families", grouped));
                return 0;
            }
            if (versions.Count == 0)
            {
                Output.Info("未获取到版本列表");
                return 0;
            }
            Output.Info("最新正式版: {0}", versions[0]);
            var showAll = pr.GetValue(all);
            var rows = grouped.Select(kv => new[]
            {
                kv.Key,
                string.Join("  ", showAll ? kv.Value : kv.Value.Take(6))
                + (!showAll && kv.Value.Count > 6 ? "  …" : ""),
            }).ToList();
            Output.Table(["家族", "版本"], rows);
            return 0;
        });
        return cmd;
    }

    // ========== doctor ==========

    public static Command Doctor()
    {
        var cmd = new Command("doctor", "环境自检（JDK / 穿透工具 / 目录）");
        cmd.SetAction(async (pr, ct) =>
        {
            var rows = new List<string[]>();
            var ok = true;

            // —— JDK：C# 版 ceobe 本体不是 JVM（ADR-019），「当前运行期 Java」一档不存在；
            //    真正要检的是「能不能给 MC 服务端找到带启动器的 JDK」。宿主 JAVA_HOME 与自管 JDK 分两行。
            var hostEnv = JavaEnv.HostJavaHomeEnv();
            string hostDetail;
            string hostStatus;
            var hostUsable = false;
            if (hostEnv is null)
            {
                hostDetail = "未设置 JAVA_HOME";
                hostStatus = "未找到：`ceobe jdk install 21` 安装自管 JDK，或设置 JAVA_HOME 指向完整 JDK";
            }
            else if (!JavaEnv.HasLauncher(hostEnv))
            {
                // ADR-018 的坑：JAVA_HOME 指到「像 JDK 却没有 bin/java.exe」的残破目录
                hostDetail = $"{hostEnv}（缺 bin/{JavaEnv.JavaExe}，残破目录不可用）";
                hostStatus = $"不可用：请换完整 JDK，或 `ceobe jdk install 21`";
            }
            else
            {
                var major = await Task.Run(() => JavaEnv.MajorOf(hostEnv, ct), ct).ConfigureAwait(false);
                hostDetail = $"Java {major?.ToString() ?? "?"}（{hostEnv}）";
                hostStatus = "OK";
                hostUsable = true;
            }
            rows.Add(["宿主 JAVA_HOME", hostDetail, hostStatus]);

            var managed = JdkStore.List(ct);
            var broken = ScanBrokenManagedDirs(ct);
            string managedDetail;
            string managedStatus;
            if (managed.Count == 0 && broken.Count == 0)
            {
                managedDetail = "无";
                managedStatus = "未找到：`ceobe jdk install <主版本>`（如 21）一键安装";
            }
            else
            {
                managedDetail = string.Join("、", managed.Select(j => $"Java {j.Major}"))
                                + (broken.Count > 0
                                    ? $"（另有残破目录缺 bin/{JavaEnv.JavaExe}: {string.Join("、", broken)}）"
                                    : "");
                managedStatus = managed.Count > 0
                    ? (broken.Count > 0
                        ? $"OK；残破目录建议删除后重装：{string.Join("、", broken)}"
                        : "OK")
                    : $"不可用：全部目录缺启动器（ADR-018），删除后 `ceobe jdk install 21` 重装";
            }
            rows.Add(["自管 JDK（~/.ceobe/jdk）", managedDetail, managedStatus]);
            var jdkUsable = hostUsable || managed.Count > 0;
            ok &= jdkUsable;

            // —— 穿透工具：可用性判定以 Core provider 的 which 探测为唯一权威，
            //    查不到时把 provider 的「下一步怎么办」原样展示，不乐观报绿。
            var cf = new CloudflareQuickTunnel();
            rows.Add(["cloudflared（Web 控制台穿透）", cf.Available ? "已安装" : "未找到",
                cf.Available ? "OK" : "可选：" + cf.UnavailableReason]);
            var ssh = new PinggyTunnel();
            rows.Add(["ssh（Pinggy 玩家通道）", ssh.Available ? "已安装" : "未找到",
                ssh.Available ? "OK" : ssh.UnavailableReason]);
            var ts = new TailscaleTunnel();
            rows.Add(["tailscale（长期联机首选）", ts.Available ? "已连接" : "未找到",
                ts.Available ? "OK" : "可选：" + ts.UnavailableReason]);

            var lan = LanDirect.LocalAddresses();
            rows.Add(["局域网地址", lan.Count > 0 ? string.Join("、", lan) : "无",
                lan.Count > 0 ? "OK" : "未连接任何网络"]);

            var home = CeobeHome.Root();
            var writable = IsHomeWritable(home);
            rows.Add(["ceobe 主目录", home, writable ? "OK" : "不可写：检查目录权限，或用 settings.json 的 homePath 改址"]);
            ok &= writable;

            // 整改项 3 的 Windows 专属雷：数据目录落在 OneDrive 下会被高频写爆云盘
            var oneDrive = AppSettings.IsPathUnderOneDrive(home);
            if (oneDrive)
            {
                rows.Add(["OneDrive 重定向", "数据目录位于 OneDrive 同步范围",
                    "警告：世界文件高频写入会打爆云盘，建议把 homePath 移出 OneDrive"]);
            }

            var instances = CoreWiring.NewManager().List().Count;
            rows.Add(["已有实例", instances.ToString(), "—"]);

            if (Output.Json)
            {
                Output.JsonValue(Output.Map("ok", ok, "checks",
                    rows.Select(r => Output.Map("check", r[0], "detail", r[1], "status", r[2])).ToList()));
            }
            else
            {
                Output.Table(["检查项", "详情", "结论"], rows);
                Output.Info(ok ? "环境可用 ✓" : "存在问题，请按上表处理");
                if (oneDrive) Output.Warn("数据目录位于 OneDrive 同步范围，建议迁移（见上表）");
            }
            return ok ? 0 : 1;
        });
        return cmd;
    }

    /// <summary>列出自管 JDK 仓库里「存在但缺启动器」的残破目录（ADR-018 守卫的反向巡检）。</summary>
    private static List<string> ScanBrokenManagedDirs(CancellationToken ct)
    {
        var broken = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(JdkStore.Root()))
            {
                ct.ThrowIfCancellationRequested();
                if (!JavaEnv.HasLauncher(dir)) broken.Add(Path.GetFileName(dir));
            }
        }
        catch (Exception)
        {
            // 扫描失败不致命：与 JdkStore.List 的容错语义一致
        }
        return broken;
    }

    /// <summary>主目录可写探测：实际试写一个临时文件（仅 Directory 存在性不足以判「可写」）。</summary>
    private static bool IsHomeWritable(string home)
    {
        try
        {
            CeobeHome.Ensure(home);
            var probe = Path.Combine(home, ".doctor-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ========== web ==========

    public static Command Web()
    {
        var port = new Option<int>("--port")
        {
            DefaultValueFactory = _ => 0,
            Description = "监听端口（0=随机）",
        };
        var instance = new Argument<string?>("实例")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "可选：直接把某实例的 CF 通道也开启",
        };

        var cmd = new Command("web", "以无头方式运行 Web 控制台（供 CF 隧道对外暴露）") { port, instance };
        cmd.SetAction(async (pr, ct) =>
        {
            var n = pr.GetValue(instance);
            var manager = CoreWiring.NewManager();
            await using var web = new WebConsole(manager, pr.GetValue(port));
            Output.Info("Web 控制台已启动: {0}", web.BaseUrl);

            // 与 Java 版一致：实例存在性先行校验（不存在直接抛），仅当控制通道可用才开 CF 通道；
            // 本进程不要求 MC 实例受控运行——CF 通道指向的是这里刚启动的 Web 控制台端口。
            TunnelManager? tunnels = null;
            if (n is not null)
            {
                manager.Require(n);
                if (InstanceControl.IsAvailable(manager.Dir(n)))
                {
                    tunnels = new TunnelManager(manager);
                    tunnels.WebConsolePort(web.Port);
                    var session = await tunnels.UpAsync(n, TunnelKind.CLOUDFLARE, ct)
                        .ConfigureAwait(false);
                    Output.Info("Cloudflare 通道: {0}", session.PublicUrl ?? "(未返回地址)");
                }
            }

            Output.Info("按 Ctrl+C 结束。");
            // Ctrl+C 优雅退出：吞掉系统默认终止，让 await using 把监听器与隧道关掉
            // （Java 版靠 JVM 退出顺带杀子进程；C# 子进程不会随宿主自动退出，必须显式 Down）。
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCancel(object? _, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stopped.TrySetResult();
            }
            Console.CancelKeyPress += OnCancel;
            using var reg = ct.Register(() => stopped.TrySetResult());
            try
            {
                await stopped.Task.ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= OnCancel;
                tunnels?.Close();
            }
            return 0;
        });
        return cmd;
    }
}
