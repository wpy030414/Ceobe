package im.xrl.ceobe.cli;

import picocli.CommandLine;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.ScopeType;

/**
 * ceobe CLI 根命令（docker 风格子命令）。
 */
@Command(name = "ceobe",
        version = "ceobe 0.1.0-SNAPSHOT",
        mixinStandardHelpOptions = true,
        description = "Minecraft 服务器编排系统（无头模式）",
        subcommands = {
                InstanceCommands.Ps.class,
                InstanceCommands.Create.class,
                InstanceCommands.Start.class,
                InstanceCommands.Stop.class,
                InstanceCommands.Restart.class,
                InstanceCommands.Rm.class,
                InstanceCommands.Logs.class,
                InstanceCommands.Exec.class,
                ConfigCommands.Get.class,
                ConfigCommands.Set.class,
                ConfigCommands.Ls.class,
                ModCommands.Search.class,
                ModCommands.Install.class,
                ModCommands.Rm.class,
                ModCommands.Ls.class,
                TunnelCommands.Up.class,
                TunnelCommands.Down.class,
                TunnelCommands.Status.class,
                MiscCommands.Versions.class,
                MiscCommands.Doctor.class,
                MiscCommands.Web.class
        })
public final class CeobeCli implements Runnable {

    @Option(names = {"--format"}, scope = ScopeType.INHERIT, paramLabel = "table|json",
            description = "输出格式（默认 table）")
    void setFormat(String format) {
        Output.json = "json".equalsIgnoreCase(format);
    }

    @Override
    public void run() {
        CommandLine.usage(this, System.out);
    }

    public static void main(String[] args) {
        CommandLine cmd = new CommandLine(new CeobeCli());
        cmd.setExecutionExceptionHandler((ex, commandLine, parseResult) -> {
            Output.error(ex.getMessage() == null ? ex.toString() : ex.getMessage());
            return 1;
        });
        System.exit(cmd.execute(args));
    }
}
