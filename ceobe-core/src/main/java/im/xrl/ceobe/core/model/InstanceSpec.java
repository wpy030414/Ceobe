package im.xrl.ceobe.core.model;

/** 创建实例的输入规格。 */
public record InstanceSpec(
        String name,
        ServerType serverType,
        String gameVersion,
        String loaderVersion,   // 可空 = 最新 stable loader
        int memoryMb,
        String jvmArgs,
        int port,
        String javaHome        // 可空 = 继承系统
) {
    public static final int MIN_MEM = 512;

    public InstanceSpec {
        if (name == null || !name.matches("[a-z0-9_\\-]{1,32}")) {
            throw new CeobeSpec("实例名只能包含小写字母/数字/下划线/连字符，长度 1-32");
        }
        if (memoryMb < MIN_MEM) {
            throw new CeobeSpec("内存至少 %dMB".formatted(MIN_MEM));
        }
        if (port < 1 || port > 65535) {
            throw new CeobeSpec("端口必须在 1-65535 之间");
        }
    }

    /** 面向用户的参数校验错误（用法错误，非系统故障）。 */
    public static final class CeobeSpec extends RuntimeException {
        public CeobeSpec(String message) {
            super(message);
        }
    }
}
