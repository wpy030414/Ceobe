package im.xrl.ceobe.cli;

import picocli.CommandLine;
import picocli.CommandLine.Command;
import picocli.CommandLine.Option;
import picocli.CommandLine.ScopeType;

/**
 * ceobe CLI 根命令（docker 风格子命令）。
 */
@Command(name = "ceobe",
        versionProvider = CeobeCli.VersionProvider.class,
        mixinStandardHelpOptions = true,
        description = "Minecraft 服务器编排系统（无头模式）",
        subcommands = {
                InstanceCommands.Ps.class,
                InstanceCommands.Create.class,
                InstanceCommands.Start.class,
                InstanceCommands.Stop.class,
                InstanceCommands.Restart.class,
                InstanceCommands.Rm.class,
                InstanceCommands.Update.class,
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

    /**
     * 版本号来源：打包清单的 {@code Implementation-Version}（由根 pom.xml 的版本号过滤注入，
     * 见 ceobe-cli/pom.xml 的 shade manifestEntries）——故根 pom 仍是唯一权威。
     * 从 classes 直跑（IDE / 测试）时清单缺失，回退为 {@code dev}。
     */
    public static class VersionProvider implements CommandLine.IVersionProvider {
        @Override
        public String[] getVersion() {
            String v = CeobeCli.class.getPackage().getImplementationVersion();
            return new String[]{"ceobe " + (v == null || v.isBlank() ? "dev" : v)};
        }
    }

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
