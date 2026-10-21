using System.Diagnostics;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using Ceobe.Core.Config;
using Ceobe.Core.Jdk;
using Ceobe.Core.Model;
using Ceobe.Core.Providers;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// 实例命令组：ps / create / start / stop / restart / rm / update / logs / exec。
/// 逐条对齐 Java 版 <c>ceobe-cli/InstanceCommands</c>（docker 风格语法与提示文案不变）。
/// </summary>
public static class InstanceCommands
{
    // ========== ps ==========

    public static Command Ps()
    {
        var all = new Option<bool>("--all") { Description = "包含已停止实例" };
        all.Aliases.Add("-a");

        var cmd = new Command("ps", "列出实例（默认仅运行中）") { all };
        cmd.Aliases.Add("ls");
        cmd.Aliases.Add("list");
        cmd.SetAction(pr =>
        {
            var manager = CoreWiring.NewManager();
            var statuses = manager.List();
            if (!pr.GetValue(all)) statuses = [.. statuses.Where(s => s.State.IsLive())];

            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instances", statuses.Select(s => Output.Map(
                    "name", s.Meta.Name,
                    "type", s.Meta.ServerType.ToString(),
                    "version", s.Meta.GameVersion,
                    "state", s.State.ToString(),
                    "pid", s.Pid,
                    "port", s.Meta.Port,
                    "address", s.AddressLine,
                    "pinggy", s.PinggyAddress,
                    "cloudflare", s.CloudflareUrl)).ToList()));
                return 0;
            }
            if (statuses.Count == 0)
            {
                Output.Info(manager.List().Count > 0
                    ? "无运行中的实例（`ceobe ps -a` 显示全部）"
                    : "暂无实例。用 `ceobe create <名称> --type paper` 创建一个。");
                return 0;
            }
            var rows = statuses.Select(s => new[]
            {
                s.Meta.Name, s.Meta.ServerType.ToString(), s.Meta.GameVersion, s.State.ZhLabel(),
                s.State.IsLive() ? s.Pid.ToString() : "-", s.Meta.Port.ToString(), s.AddressLine,
            }).ToList();
            Output.Table(["名称", "类型", "版本", "状态", "PID", "端口", "地址"], rows);
            return 0;
        });
        return cmd;
    }

    // ========== create ==========

    public static Command Create()
    {
        var name = new Argument<string>("name") { Description = "实例名（小写字母/数字/_/-）" };
        var type = new Option<string>("--type")
        {
            DefaultValueFactory = _ => "paper",
            Description = "服务端类型: vanilla | paper | fabric | neoforge | purpur（默认 paper）",
        };
        var version = new Option<string?>("--version") { Description = "游戏版本（默认最新正式版）" };
        var loader = new Option<string?>("--loader")
        {
            Description = "加载器版本（Fabric/NeoForge；默认最新 stable）",
        };
        var mem = new Option<string>("--mem") { DefaultValueFactory = _ => "2G", Description = "内存上限，如 4G / 4096M" };
        var port = new Option<string>("--port")
        {
            DefaultValueFactory = _ => "25565",
            Description = "服务器端口；auto = 自动选取空闲端口（默认 25565）",
        };
        var jvm = new Option<string>("--jvm") { DefaultValueFactory = _ => "", Description = "附加 JVM 参数" };
        var javaHome = new Option<string?>("--java-home") { Description = "指定 JRE 目录" };

        var cmd = new Command("create", "创建实例并安装服务端")
        {
            name, type, version, loader, mem, port, jvm, javaHome,
        };
        cmd.SetAction(async (pr, ct) =>
        {
            // 先做**纯本地**校验再联网：实例名/内存/端口写错时应立刻失败。
            // Java 版是先 listVersions() 再 new InstanceSpec(...)，名字打错也要白等一次
            // 版本查询（无网时是干等超时）。既然在重写，就把快速失败做对。
            //
            // 校验必须走 InstanceSpec.Validate()（抛 CeobeSpec → 退出码 2，用法错误档），
            // 而不是 InstanceManager.ValidateName（抛 CeobeException → 退出码 1，系统故障档）：
            // 用户打错名字是用法错误，Java 版 create 路径的退出码就是 2，不能改语义。
            var serverType = CliParse.Type(pr.GetValue(type)!);
            var manager = CoreWiring.NewManager();
            var localPort = CliParse.ResolvePort(pr.GetValue(port), manager);
            // 版本号未知时用占位值构造一次 spec，只为触发名字/内存/端口的本地校验
            new InstanceSpec(pr.RequiredTrimmed(name), serverType, "0", pr.GetValue(loader),
                CliParse.Memory(pr.GetValue(mem)!), pr.GetValue(jvm), localPort, pr.GetValue(javaHome))
                .Validate();

            var provider = ProviderRegistry.Of(serverType);
            var gameVersion = pr.GetValue(version);
            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                var versions = await provider.ListVersionsAsync(ct).ConfigureAwait(false);
                gameVersion = versions.FirstOrDefault()
                              ?? throw new CeobeException("获取版本列表失败");
                Output.Info("未指定版本，使用最新正式版: {0}", gameVersion);
            }

            var spec = new InstanceSpec(
                pr.RequiredTrimmed(name),
                serverType,
                gameVersion,
                pr.GetValue(loader),
                CliParse.Memory(pr.GetValue(mem)!),
                pr.GetValue(jvm),
                CliParse.ResolvePort(pr.GetValue(port), manager),
                pr.GetValue(javaHome));

            var progress = CliParse.ProgressBar();
            var meta = await manager.CreateAsync(spec, progress, ct).ConfigureAwait(false);
            CliParse.ProgressDone();

            if (Output.Json)
            {
                Output.JsonValue(Output.Map(
                    "name", meta.Name, "type", meta.ServerType.ToString(),
                    "version", meta.GameVersion, "loader", meta.LoaderVersion,
                    "port", meta.Port, "memoryMb", meta.MemoryMb,
                    "dir", manager.Dir(pr.RequiredTrimmed(name))));
            }
            else
            {
                Output.Info("实例 {0} 创建完成（{1} {2}，端口 {3}，内存 {4}MB）",
                    meta.Name, meta.ServerType, meta.GameVersion, meta.Port, meta.MemoryMb);
                Output.Info("启动: ceobe start {0}", meta.Name);
            }

            // JDK 可用性提醒：创建成功但起不来是最糟的体验，这里先说清楚
            var required = await JavaRequirement.RequiredMajorAsync(meta.GameVersion, ct).ConfigureAwait(false);
            var check = JavaResolver.Resolve(meta.GameVersion, required, pr.GetValue(javaHome), ct);
            if (!check.Ok) Output.Warn("{0}", check.Message ?? "未找到可用 Java 运行时");
            else if (!string.IsNullOrEmpty(check.Message)) Output.Info("{0}", check.Message);
            return 0;
        });
        return cmd;
    }

    // ========== start ==========

    public static Command Start()
    {
        var name = new Argument<string>("name");
        var detach = new Option<bool>("--detach") { Description = "后台运行（独立监督进程）" };
        detach.Aliases.Add("-d");

        var cmd = new Command("start", "启动实例（默认前台；-d 后台监督进程）") { name, detach };
        cmd.SetAction(async (pr, ct) =>
        {
            var n = pr.RequiredTrimmed(name);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(n);

            // JDK 提醒（对齐 Java 版）：不阻断，但让用户先看到问题
            var required = await JavaRequirement.RequiredMajorAsync(status.Meta.GameVersion, ct)
                .ConfigureAwait(false);
            var check = JavaResolver.Resolve(status.Meta.GameVersion, required, status.Meta.JavaHome, ct);
            if (!check.Ok) Output.Warn("{0}", check.Message ?? "未找到可用 Java 运行时");

            if (status.State.IsLive())
            {
                throw new CeobeException($"实例 {n} 已在运行（{status.State.ZhLabel()}）");
            }

            if (!pr.GetValue(detach))
            {
                // 前台：当前进程直接当监督进程
                return await Supervisor.RunAsync(n, foreground: true, ct).ConfigureAwait(false);
            }
            await StartDetachedAsync(manager, n, ct).ConfigureAwait(false);
            ReportRunning(n, manager);
            return 0;
        });
        return cmd;
    }

    /// <summary>后台拉起监督进程并等待服务端就绪（容忍控制通道尚未建立的窗口期）。</summary>
    internal static async Task StartDetachedAsync(InstanceManager manager, string name, CancellationToken ct)
    {
        var dir = manager.Dir(name);
        // 清理上一轮遗留的控制通道信息，避免读到已失效端口
        InstanceControl.Clear(dir);
        SpawnSupervisor(dir, name);

        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = InstanceControl.State(dir);
                if (state == InstanceState.RUNNING) return;
            }
            catch (CeobeException)
            {
                // 通道尚未就绪（拒绝连接 / 超时）——继续等待
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        throw new CeobeException($"实例 {name} 启动超时，请查看 {Path.Combine(dir, "supervisor.log")}");
    }

    internal static void ReportRunning(string name, InstanceManager manager)
    {
        var address = "127.0.0.1:" + manager.Meta(name).Port;
        if (Output.Json)
        {
            Output.JsonValue(Output.Map("name", name, "state", "RUNNING", "address", address));
        }
        else
        {
            Output.Info("实例 {0} 已就绪 ✓  本机地址: {1}", name, address);
            Output.Info("开启玩家通道: ceobe tunnel up {0} --kind pinggy", name);
        }
    }

    /// <summary>
    /// 拉起监督进程。C# 版比 Java 版简单得多：重新拉起**自身可执行文件** + 隐藏的
    /// <c>supervise</c> 子命令，不需要 classpath 也不需要 java 启动器
    /// （Java 版这里要靠 JavaEnv 找 launcher，且踩过 ADR-018 的坑）。
    /// </summary>
    private static void SpawnSupervisor(string instanceDir, string name)
    {
        var self = Environment.ProcessPath
                   ?? throw new CeobeException("无法定位自身可执行文件，无法派生监督进程");

        var psi = new ProcessStartInfo
        {
            FileName = self,
            WorkingDirectory = instanceDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 把父进程的数据目录传给子进程，否则子进程会回落到 %USERPROFILE%\.ceobe
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("supervise");
        psi.ArgumentList.Add(name);
        var home = Ceobe.Core.Store.CeobeHome.Override;
        if (!string.IsNullOrEmpty(home))
        {
            psi.Environment["CEOBE_HOME"] = home;
        }

        var p = Process.Start(psi) ?? throw new CeobeException("监督进程未能启动");
        // 监督进程的 stdout/stderr 追加到 supervisor.log（排障用），必须持续泵否则会写满管道缓冲区导致挂死
        var log = Path.Combine(instanceDir, "supervisor.log");
        _ = PumpToFileAsync(p.StandardOutput, log);
        _ = PumpToFileAsync(p.StandardError, log);
    }

    private static async Task PumpToFileAsync(StreamReader reader, string logFile)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // 日志落盘失败不影响实例运行
        }
    }

    // ========== stop ==========

    public static Command Stop()
    {
        var name = new Argument<string>("name");
        var force = new Option<bool>("--force") { Description = "跳过优雅停止，直接终止" };

        var cmd = new Command("stop", "优雅停止实例") { name, force };
        cmd.SetAction(async (pr, ct) =>
        {
            var n = pr.RequiredTrimmed(name);
            var manager = CoreWiring.NewManager();
            var status = manager.Require(n);
            if (!status.State.IsLive())
            {
                Output.Info("实例 {0} 未在运行", n);
                return 0;
            }
            var dir = manager.Dir(n);
            if (!InstanceControl.IsAvailable(dir))
            {
                throw new CeobeException(
                    $"实例 {n} 由不受控进程托管（无控制通道）；可用任务管理器结束 pid {status.Pid}");
            }
            if (pr.GetValue(force)) InstanceControl.Kill(dir);
            else InstanceControl.Stop(dir);

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var state = InstanceControl.State(dir);
                if (state is null or InstanceState.STOPPED or InstanceState.CRASHED)
                {
                    Output.Info("实例 {0} 已停止", n);
                    return 0;
                }
                await Task.Delay(700, ct).ConfigureAwait(false);
            }
            throw new CeobeException($"停止超时：实例 {n} 仍在运行");
        });
        return cmd;
    }

    // ========== restart ==========

    public static Command Restart()
    {
        var name = new Argument<string>("name");
        var cmd = new Command("restart", "重启实例（后台监督进程）") { name };
        cmd.SetAction(async (pr, ct) =>
        {
            var n = pr.RequiredTrimmed(name);
            var manager = CoreWiring.NewManager();
            manager.Require(n);
            var dir = manager.Dir(n);
            if (manager.Require(n).State.IsLive() && InstanceControl.IsAvailable(dir))
            {
                InstanceControl.Stop(dir);
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = InstanceControl.State(dir);
                    if (s is null || !s.Value.IsLive()) break;
                    await Task.Delay(700, ct).ConfigureAwait(false);
                }
            }
            await StartDetachedAsync(manager, n, ct).ConfigureAwait(false);
            ReportRunning(n, manager);
            return 0;
        });
        return cmd;
    }

    // ========== rm ==========

    public static Command Rm()
    {
        var name = new Argument<string>("name");
        var purge = new Option<bool>("--purge") { Description = "连同世界数据一并删除（默认仅移除注册）" };

        var cmd = new Command("rm", "删除实例") { name, purge };
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("delete");
        cmd.SetAction(pr =>
        {
            var n = pr.RequiredTrimmed(name);
            var p = pr.GetValue(purge);
            CoreWiring.NewManager().Remove(n, p);
            Output.Info("实例 {0} 已删除{1}", n, p ? "（含数据）" : "（世界数据保留在目录中）");
            return 0;
        });
        return cmd;
    }

    // ========== logs ==========

    public static Command Logs()
    {
        var name = new Argument<string>("name");
        var follow = new Option<bool>("--follow") { Description = "持续跟随输出" };
        follow.Aliases.Add("-f");
        var tail = new Option<int>("--tail") { DefaultValueFactory = _ => 100, Description = "初始显示行数" };

        var cmd = new Command("logs", "查看实例日志（-f 跟随）") { name, follow, tail };
        cmd.SetAction(async (pr, ct) =>
        {
            var n = pr.RequiredTrimmed(name);
            var manager = CoreWiring.NewManager();
            manager.Require(n);
            var dir = manager.Dir(n);
            var logFile = Path.Combine(dir, "console.log");
            var t = pr.GetValue(tail);

            var initial = InstanceControl.IsAvailable(dir)
                ? InstanceControl.Logs(dir, t)
                : ReadTail(logFile, t);
            foreach (var line in initial) Console.WriteLine(line);
            if (!pr.GetValue(follow)) return 0;

            var position = File.Exists(logFile) ? new FileInfo(logFile).Length : 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(400, ct).ConfigureAwait(false);
                if (File.Exists(logFile) && new FileInfo(logFile).Length > position)
                {
                    await using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(position, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, new UTF8Encoding(false));
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    {
                        Console.WriteLine(line);
                    }
                    position = new FileInfo(logFile).Length;
                }

                var live = InstanceControl.IsAvailable(dir)
                    ? InstanceControl.State(dir)?.IsLive() ?? false
                    : manager.Find(n)?.State.IsLive() ?? false;
                if (!live)
                {
                    Output.Info("—— 实例已停止，跟随结束 ——");
                    return 0;
                }
            }
        });
        return cmd;
    }

    private static List<string> ReadTail(string file, int tail)
    {
        if (!File.Exists(file)) return [];
        try
        {
            var all = File.ReadAllLines(file, new UTF8Encoding(false));
            return all.Length <= tail ? all.ToList() : all[^tail..].ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    // ========== exec ==========

    public static Command Exec()
    {
        var name = new Argument<string>("name");
        var command = new Argument<string[]>("command")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "服务端命令，如 say 你好",
        };

        var cmd = new Command("exec", "向实例控制台发送命令") { name, command };
        cmd.SetAction(pr =>
        {
            var n = pr.RequiredTrimmed(name);
            var manager = CoreWiring.NewManager();
            manager.Require(n);
            var dir = manager.Dir(n);
            var joined = string.Join(' ', pr.GetValue(command) ?? []);
            if (!InstanceControl.IsAvailable(dir))
            {
                throw new CeobeException($"实例 {n} 未在受控进程中运行，无法发送命令");
            }
            InstanceControl.Exec(dir, joined);
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("name", n, "command", joined, "sent", true));
            }
            else
            {
                Output.Info("已发送: {0}", joined);
            }
            return 0;
        });
        return cmd;
    }

    // ========== update ==========

    public static Command Update()
    {
        var name = new Argument<string>("name");
        var newName = new Option<string?>("--name") { Description = "重命名实例" };
        var mem = new Option<string?>("--mem") { Description = "内存上限，如 4G" };
        var port = new Option<string?>("--port") { Description = "端口或 auto" };
        // 「传空串可清空」必须写成 --jvm ""（空格分隔），不能写 --jvm=：
        // System.CommandLine beta5 把等号后空值判为「选项缺少所需参数」，而 picocli 接受。
        // 这里不能把 Arity 放宽成 ZeroOrOne 来兼容等号形式——实测那样 --jvm= 与 --jvm
        // 都会解析成 null，而 null 在本命令的语义是「不改动该项」，与空串「清空」必须区分
        // （否则 --mem 4G 会顺手把 jvmArgs 清掉）。取舍：保住语义，牺牲一种写法，并在帮助里说明。
        var jvm = new Option<string?>("--jvm")
        {
            Description = "附加 JVM 参数；传空串清空（须写成 --jvm \"\"，不支持 --jvm=）",
        };
        var authlib = new Option<string?>("--authlib")
        {
            Description = "authlib-injector Yggdrasil 地址；传空串关闭（须写成 --authlib \"\"）",
        };

        var cmd = new Command("update", "修改实例设置（名称/内存/端口/JVM 参数/authlib-injector）")
        {
            name, newName, mem, port, jvm, authlib,
        };
        cmd.SetAction(pr =>
        {
            var n = pr.RequiredTrimmed(name);
            var manager = CoreWiring.NewManager();
            manager.Require(n);

            var target = n;
            var renamed = pr.GetValue(newName);
            if (!string.IsNullOrWhiteSpace(renamed) && renamed != n)
            {
                manager.Rename(n, renamed);
                target = renamed;
            }

            var memMb = CliParse.MemoryOrNull(pr.GetValue(mem));
            var portNum = CliParse.PortOrNull(pr.GetValue(port), manager);
            var jvmArgs = pr.GetValue(jvm);
            var authUrl = pr.GetValue(authlib);

            if (memMb is null && portNum is null && jvmArgs is null && authUrl is null)
            {
                Output.Info("未指定任何改动（--name/--mem/--port/--jvm/--authlib）");
                return 0;
            }
            manager.UpdateInstance(target, memMb, portNum, jvmArgs, authUrl);

            var meta = manager.Meta(target);
            if (Output.Json)
            {
                Output.JsonValue(Output.Map(
                    "name", target, "memoryMb", meta.MemoryMb, "port", meta.Port,
                    "jvmArgs", meta.JvmArgs, "authlibInjectorUrl", meta.AuthlibInjectorUrl));
            }
            else
            {
                Output.Info("实例 {0} 已更新（内存 {1}MB，端口 {2}）", target, meta.MemoryMb, meta.Port);
                // 整改项 5 的元数据在此落地：改完告诉用户要不要重启
                if (meta.Port != portNum && portNum is not null)
                {
                    Output.Info("提示: server-port 属「需重启生效」项，请在停止后重新启动实例");
                }
            }
            return 0;
        });
        return cmd;
    }

    // ========== supervise（隐藏子命令：被 start -d 派生的监督进程使用）==========

    public static Command Supervise()
    {
        var name = new Argument<string>("name");
        var cmd = new Command("supervise", "监督进程入口（内部使用）") { name };
        // 不在 help 里显示：它只由 start -d 自己拉起
        cmd.Hidden = true;
        cmd.SetAction(async (pr, ct) => await Supervisor.RunAsync(pr.RequiredTrimmed(name), foreground: false, ct)
            .ConfigureAwait(false));
        return cmd;
    }
}
