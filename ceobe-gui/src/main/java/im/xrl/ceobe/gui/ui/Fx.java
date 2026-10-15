package im.xrl.ceobe.gui.ui;

import javafx.application.Platform;

import java.util.function.Consumer;
import java.util.function.Supplier;

/** JavaFX 辅助：后台任务（虚拟线程）+ UI 线程回投 + 剪贴板。 */
public final class Fx {

    /** 后台执行，结果/异常回投 UI 线程。 */
    public static <T> void background(Supplier<T> work, Consumer<T> onOk, Consumer<Throwable> onErr) {
        Thread.ofVirtual().start(() -> {
            try {
                T result = work.get();
                if (onOk != null) Platform.runLater(() -> onOk.accept(result));
            } catch (Throwable t) {
                if (onErr != null) Platform.runLater(() -> onErr.accept(t));
            }
        });
    }

    public static void background(Runnable work) {
        background(() -> {
            work.run();
            return null;
        }, null, null);
    }

    public static void copy(String text) {
        var clipboard = javafx.scene.input.Clipboard.getSystemClipboard();
        var content = new javafx.scene.input.ClipboardContent();
        content.putString(text);
        clipboard.setContent(content);
    }

    private Fx() {
    }
}
