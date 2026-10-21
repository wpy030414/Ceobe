using System.CommandLine;
using Ceobe.Core.Jdk;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// JDK 自检与一键安装命令族。逐条对齐 Java 版 <c>ceobe-cli/JdkCommands</c>。
///
/// ADR-019 差异：Java 版 list 首行显示「当前运行期 Java」（ceobe 自己跑在 JVM 上），
/// C# 版进程不是 JVM，没有可继承的运行期——改为显示宿主 JAVA_HOME 的探测结果，
/// 语义上回答同一个问题：「不装自管 JDK 时，服务端会用谁」。
/// </summary>
public static class JdkCommands
{
    public static Command Root()
    {
        var cmd = new Command("jdk", "JDK 检查、一键安装与按实例指定")
        {
            ListCmd(), Install(), Required(), Use(),
        };
        cmd.SetAction(_ =>
        {
            Output.Info("用法: ceobe jdk <list | install <主版本> | required <游戏版本> | use <实例> <主版本>>");
            return 0;
        });
        return cmd;
    }

    // ========== list ==========

    private static Command ListCmd()
    {
        var cmd = new Command("list", "列出自管 JDK 与当前运行期 Java");
        cmd.Aliases.Add("ls");
        cmd.SetAction(pr =>
        {
            var jdks = JdkStore.List();
            var hostHome = JavaEnv.HostJavaHome();
            var hostMajor = hostHome is null ? null : JavaEnv.MajorOf(hostHome);
            if (Output.Json)
            {
                var l = jdks.Select(j => Output.Map(
                    "major", j.Major, "name", j.Name, "home", j.Home)).ToList();
                Output.JsonValue(Output.Map(
                    "hostJavaHome", hostHome, "hostMajor", (object?)hostMajor, "installed", l));
                return 0;
            }
            Output.Info("宿主 Java（JAVA_HOME）: {0}", hostHome is not null
                ? $"Java {hostMajor?.ToString() ?? "?"}（{hostHome}）"
                : "未找到（ceobe 不依赖宿主运行期；服务端可用 `ceobe jdk install <n>` 自管）");
            if (jdks.Count == 0)
            {
                Output.Info("尚未安装 ceobe 自管 JDK。安装: ceobe jdk install <主版本>");
                return 0;
            }
            var rows = jdks.Select(j => new[] { j.Major.ToString(), j.Name, j.Home }).ToList();
            Output.Table(["Java", "名称", "路径"], rows);
            return 0;
        });
        return cmd;
    }

    // ========== install ==========

    private static Command Install()
    {
        var major = new Argument<int>("主版本") { Description = "如 21 / 17 / 8" };
        var cmd = new Command("install", "一键安装指定主版本的 Temurin JDK（按平台自动选包）") { major };
        cmd.SetAction(async (pr, ct) =>
        {
            var m = pr.GetValue(major);
            var progress = CliParse.ProgressBar();
            var home = await JdkInstaller.InstallAsync(m, progress, ct).ConfigureAwait(false);
            CliParse.ProgressDone();
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("major", m, "home", home));
            }
            else
            {
                Output.Info("已安装 Java {0} → {1}", m, home);
                Output.Info("新建实例会自动选用它；已有实例用 `ceobe jdk use <实例> {0}`", m);
            }
            return 0;
        });
        return cmd;
    }

    // ========== required ==========

    private static Command Required()
    {
        var version = new Argument<string>("游戏版本");
        var cmd = new Command("required", "查询某游戏版本所需的 Java 主版本") { version };
        cmd.SetAction(async (pr, ct) =>
        {
            var v = pr.RequiredTrimmed(version);
            var req = await JavaRequirement.RequiredMajorAsync(v, ct).ConfigureAwait(false);
            if (Output.Json) Output.JsonValue(Output.Map("version", v, "requiredJava", req));
            else Output.Info("{0} 需要 Java {1}", v, req);
            return 0;
        });
        return cmd;
    }

    // ========== use ==========

    private static Command Use()
    {
        var instance = new Argument<string>("实例");
        var major = new Argument<int>("主版本");
        var cmd = new Command("use", "把某实例的 Java 指向自管 JDK（按主版本）") { instance, major };
        cmd.SetAction(pr =>
        {
            var n = pr.RequiredTrimmed(instance);
            var m = pr.GetValue(major);
            var manager = CoreWiring.NewManager();
            if (manager.Require(n).State.IsLive())
            {
                throw CeobeException.Of("实例 {0} 正在运行，请先停止", n);
            }
            var home = JdkStore.FindMajor(m)
                       ?? throw CeobeException.Of("未安装 Java {0}；先运行 `ceobe jdk install {0}`", m);
            manager.SetJavaHome(n, home);
            Output.Info("实例 {0} 的 Java 已指向 Java {1} → {2}", n, m, home);
            return 0;
        });
        return cmd;
    }
}
