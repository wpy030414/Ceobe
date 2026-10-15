package im.xrl.ceobe.core.event;

/** core 事件：（GUI/CLI/Web 订阅同一套）。 */
public sealed interface InstanceEvent {

    String instance();

    record LogLine(String instance, String line) implements InstanceEvent {
    }

    record StateChanged(String instance, im.xrl.ceobe.core.model.InstanceState state, int exitCode)
            implements InstanceEvent {
    }

    record TunnelChanged(String instance, String kind, String publicUrl) implements InstanceEvent {
    }
}
