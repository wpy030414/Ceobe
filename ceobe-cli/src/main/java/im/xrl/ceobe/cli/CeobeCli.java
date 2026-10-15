package im.xrl.ceobe.cli;

import picocli.CommandLine;
import picocli.CommandLine.Command;

/**
 * ceobe CLI 入口。docker 风格子命令在 im.xrl.ceobe.cli.cmd 下逐个注册。
 */
@Command(name = "ceobe",
        version = "ceobe 0.1.0-SNAPSHOT",
        mixinStandardHelpOptions = true,
        description = "Minecraft 服务器编排系统（无头模式）")
public final class CeobeCli implements Runnable {

    @Override
    public void run() {
        CommandLine.usage(this, System.out);
    }

    public static void main(String[] args) {
        int code = new CommandLine(new CeobeCli()).execute(args);
        System.exit(code);
    }
}
