package im.xrl.ceobe.gui;

/**
 * 启动引导类。
 * <p>主类不继承 {@link javafx.application.Application}，从而允许以 classpath 方式
 * 运行打包后的 fat jar（否则 JVM 会以「缺少 JavaFX 运行时组件」拒绝启动）。
 */
public final class Launcher {

    public static void main(String[] args) {
        CeobeGui.main(args);
    }

    private Launcher() {
    }
}
