package im.xrl.ceobe.core.event;

/** 事件监听器。实现方需自行保证线程安全（事件来自日志泵/看门狗线程）。 */
@FunctionalInterface
public interface InstanceListener {
    void onEvent(InstanceEvent event);
}
