package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.model.InstanceMeta;
import im.xrl.ceobe.core.model.InstanceState;

/** 实例状态视图（CLI/GUI 展示用，不暴露进程细节）。 */
public record InstanceStatus(
        InstanceMeta meta,
        InstanceState state,
        long pid,               // -1 = 无
        boolean ownedByThisProcess,
        String pinggyAddress,
        String cloudflareUrl
) {
    public String addressLine() {
        if (pinggyAddress != null) return pinggyAddress;
        return "本机 127.0.0.1:" + meta.port;
    }
}
