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
                ConfigCommands.Root.class,
                ModCommands.Root.class,
                ModpackCommands.Root.class,
                TunnelCommands.Root.class,
                MiscCommands.Versions.class,
                MiscCommands.Doctor.class,
                MiscCommands.Web.class,
                JdkCommands.Root.class
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
        // Windows 控制台默认非 UTF-8，统一以 UTF-8 输出，避免中文乱码
        System.setOut(new java.io.PrintStream(
                new java.io.FileOutputStream(java.io.FileDescriptor.out), true,
                java.nio.charset.StandardCharsets.UTF_8));
        System.setErr(new java.io.PrintStream(
                new java.io.FileOutputStream(java.io.FileDescriptor.err), true,
                java.nio.charset.StandardCharsets.UTF_8));

        CommandLine cmd = new CommandLine(new CeobeCli());
        cmd.setExecutionExceptionHandler((ex, commandLine, parseResult) -> {
            Output.error(ex.getMessage() == null ? ex.toString() : ex.getMessage());
            return 1;
        });
        System.exit(cmd.execute(args));
    }
}
