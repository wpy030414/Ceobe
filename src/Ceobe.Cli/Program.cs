using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text;
using Ceobe.Core.Util;
using Ceobe.Cli.Commands;

namespace Ceobe.Cli;

/// <summary>
/// ceobe CLI 根命令（docker 风格子命令）。等价 Java 版 <c>ceobe-cli/CeobeCli</c>。
///
/// 退出码约定（对齐 Java 版 executionExceptionHandler，并区分用法错误）：
/// <list type="number">
/// <item>0 成功</item>
/// <item>1 系统故障（<see cref="CeobeException"/>：下载失败、端口占用、进程起不来）</item>
/// <item>2 用法错误（<see cref="CeobeSpec"/>：实例名非法、内存低于下限）</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>全局输出格式（<c>--format json</c> 时置位）。</summary>
    private static readonly Option<string> FormatOption = new("--format")
    {
        // Recursive = picocli 的 ScopeType.INHERIT：子命令也能读到根级选项
        Recursive = true,
        DefaultValueFactory = _ => "table",
        Description = "输出格式：table（人类可读）| json（机器可读）",
    };

    public static async Task<int> Main(string[] args)
    {
        Output.ConfigureEncoding();

        var root = BuildRoot();
        var config = new CommandLineConfiguration(root);

        // 根选项要在子命令 action 之前生效，故先解析一次取 --format
        try
        {
            var preParse = root.Parse(args, config);
            if (preParse.Errors.Count == 0)
            {
                Output.Json = string.Equals(preParse.GetValue(FormatOption), "json",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            // 解析失败交给下面的正式 Invoke 报错，这里不重复输出
        }

        try
        {
            return await root.Parse(args, config).InvokeAsync().ConfigureAwait(false);
        }
        catch (CeobeSpec e)
        {
            Output.Error(e.Message);
            return 2;
        }
        catch (CeobeException e)
        {
            Output.Error(e.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Output.Error("操作已取消");
            return 130;
        }
        catch (Exception e)
        {
            Output.Error(e.Message);
            if (Environment.GetEnvironmentVariable("CEOBE_DEBUG") == "1")
            {
                Console.Error.WriteLine(e.StackTrace);
            }
            return 1;
        }
    }

    private static RootCommand BuildRoot()
    {
        var root = new RootCommand("ceobe — Minecraft 服务器编排系统（无头模式）")
        {
            FormatOption,
        };

        // 17 个子命令（与 Java 版 CeobeCli 的 subcommands 逐一对应）
        root.Add(InstanceCommands.Ps());
        root.Add(InstanceCommands.Create());
        root.Add(InstanceCommands.Start());
        root.Add(InstanceCommands.Stop());
        root.Add(InstanceCommands.Restart());
        root.Add(InstanceCommands.Rm());
        root.Add(InstanceCommands.Update());
        root.Add(InstanceCommands.Logs());
        root.Add(InstanceCommands.Exec());
        root.Add(ConfigCommands.Root());
        root.Add(ModCommands.Root());
        root.Add(ModpackCommands.Root());
        root.Add(TunnelCommands.Root());
        root.Add(MiscCommands.Versions());
        root.Add(MiscCommands.Doctor());
        root.Add(MiscCommands.Web());
        root.Add(JdkCommands.Root());

        // 隐藏入口：被 `start -d` 派生的监督进程使用（C# 版重新拉起自身可执行文件，
        // 不需要 Java 版的 classpath + java 启动器那套）
        root.Add(InstanceCommands.Supervise());
        return root;
    }

    /// <summary>
    /// 版本号：取程序集 <c>AssemblyInformationalVersion</c>（由 Directory.Build.props 的
    /// <c>&lt;Version&gt;</c> 注入，故根 props 仍是唯一权威，对齐 ADR-017 与 MIGRATION 硬契约 7）。
    /// IDE / 未打包直跑时可能缺失，回退 <c>dev</c>（对齐 Java 版从 classes 直跑的兜底）。
    /// </summary>
    public static string VersionString()
    {
        var asm = Assembly.GetExecutingAssembly();
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(v)) return "dev";
        // InformationalVersion 形如 "26.10.21.01+abc123"（含 source revision），去掉 + 后缀
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>把 CalVer 的点分四段还原成展示形态（26.10.21.01 → 26.10.21-01）。见 ADR-019 版本决策。</summary>
    public static string DisplayVersion()
    {
        var v = VersionString();
        var parts = v.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsDigit))
            ? $"{parts[0]}.{parts[1]}.{parts[2]}-{parts[3]}"
            : v;
    }
}
