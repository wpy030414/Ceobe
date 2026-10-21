using System.CommandLine;
using Ceobe.Core.Config;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Cli.Commands;

/// <summary>
/// server.properties 配置命令族（直接操作文件，无需实例在运行）。
/// 逐条对齐 Java 版 <c>ceobe-cli/ConfigCommands</c>：命令名、参数位、提示文案不变；
/// 整改项 5 在 <c>ls</c> 表格新增「需重启」列（取自 <see cref="PropKey.Policy"/>）。
/// </summary>
public static class ConfigCommands
{
    public static Command Root()
    {
        var cmd = new Command("config", "查看/修改 server.properties")
        {
            Get(), Set(), Ls(),
        };
        cmd.SetAction(_ =>
        {
            Output.Info("用法: ceobe config <get|set|ls> <实例> …");
            return 0;
        });
        return cmd;
    }

    // ========== get ==========

    private static Command Get()
    {
        var instance = new Argument<string>("实例");
        var key = new Argument<string>("键");
        var cmd = new Command("get", "读取单个配置项") { instance, key };
        cmd.SetAction(pr =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var k = pr.RequiredTrimmed(key);
            var props = Load(inst);
            var value = props.Get(k);
            if (value is null)
            {
                var schema = PropSchema.Key(k);
                if (schema is null) throw CeobeException.Of("未知配置项: {0}", k);
                value = schema.Def;
            }
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "key", k, "value", value));
            }
            else
            {
                Console.WriteLine(value);
            }
            return 0;
        });
        return cmd;
    }

    // ========== set ==========

    private static Command Set()
    {
        var instance = new Argument<string>("实例");
        var key = new Argument<string>("键");
        var value = new Argument<string>("值");
        var cmd = new Command("set", "修改配置项（服务器运行时需重启生效）") { instance, key, value };
        cmd.SetAction(pr =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var k = pr.RequiredTrimmed(key);
            var v = pr.RequiredTrimmed(value);
            var props = Load(inst);
            // 校验失败时 Core 的 Set 直接抛带 schema 中文原因的 CeobeException
            // （如「视距（区块） 需在 3-32 之间」），满足整改项 5 对报错可读性的要求。
            props.Set(k, v);
            props.Save();
            var schema = PropSchema.Key(k);
            Output.Info("已设置 {0} = {1}{2}", k, v, schema is null ? "" : $"（{schema.Zh}）");
            return 0;
        });
        return cmd;
    }

    // ========== ls ==========

    private static Command Ls()
    {
        var instance = new Argument<string>("实例");
        var group = new Option<string?>("--group") { Description = "仅显示某分组（如 基础/网络/性能）" };
        var cmd = new Command("ls", "列出配置项（按 schema 分组）") { instance, group };
        cmd.SetAction(pr =>
        {
            var inst = pr.RequiredTrimmed(instance);
            var grp = pr.GetValue(group);
            var props = Load(inst);
            var entries = new List<(PropKey Key, string Value)>();
            foreach (var g in PropSchema.Groups())
            {
                if (grp is not null && grp != g) continue;
                foreach (var k in PropSchema.InGroup(g))
                {
                    entries.Add((k, props.Contains(k.Key) ? props.Get(k.Key)! : k.Def));
                }
            }
            if (Output.Json)
            {
                Output.JsonValue(Output.Map("instance", inst, "properties",
                    entries.Select(e => Output.Map(
                        "group", e.Key.Group, "key", e.Key.Key, "label", e.Key.Zh,
                        "value", e.Value, "policy", e.Key.Policy.ToString())).ToList()));
                return 0;
            }
            var rows = entries
                .Select(e => new[] { e.Key.Group, e.Key.Key, e.Key.Zh, e.Value, PolicyLabel(e.Key.Policy) })
                .ToList();
            Output.Table(["分组", "键", "含义", "当前值", "需重启"], rows);
            return 0;
        });
        return cmd;
    }

    /// <summary>「需重启」列的中文标签（整改项 5）。PropKey.Policy 的语义见 RestartPolicy 文档。</summary>
    private static string PolicyLabel(RestartPolicy p) => p switch
    {
        RestartPolicy.Reload => "可热重载",
        RestartPolicy.Live => "立即生效",
        RestartPolicy.Unknown => "未知（按需重启）",
        _ => "需重启",
    };

    private static ServerPropertiesFile Load(string instance)
    {
        var manager = CoreWiring.NewManager();
        var dir = manager.Dir(instance);
        manager.Require(instance);
        var file = Path.Combine(dir, "server.properties");
        if (!File.Exists(file))
        {
            throw CeobeException.Of("实例 {0} 尚无 server.properties（先启动一次）", instance);
        }
        return ServerPropertiesFile.Load(file);
    }
}
