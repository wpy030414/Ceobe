package im.xrl.ceobe.core.model;

import com.fasterxml.jackson.annotation.JsonInclude;
import im.xrl.ceobe.core.mods.ModEntry;

import java.time.Instant;
import java.util.ArrayList;
import java.util.List;

/** 实例持久化元数据（instance.json）。 */
@JsonInclude(JsonInclude.Include.NON_NULL)
public final class InstanceMeta {
    public String name;
    public ServerType serverType;
    public String gameVersion;
    public String loaderVersion;      // fabric loader 版本，其余类型为 null
    public int memoryMb = 2048;
    public String jvmArgs = "";
    public int port = 25565;
    public String javaHome;           // null = 继承当前 JVM 的 JAVA_HOME
    public String launchJar = "server.jar";
    /** 非空时改用参数文件启动（NeoForge）：{@code java @<launchArgsFile> nogui}。 */
    public String launchArgsFile;
    /** 非空时注入 authlib-injector 做三方鉴权：{@code -javaagent:authlib-injector.jar=<url>}。 */
    public String authlibInjectorUrl;
    public Instant createdAt = Instant.now();
    public List<ModEntry> mods = new ArrayList<>();
    /** 最近一次各通道公网地址（便于重启后展示历史）。 */
    public String lastPinggyAddress;
    public String lastCloudflareUrl;

    public InstanceMeta() {
    }

    public InstanceMeta(String name, ServerType serverType, String gameVersion, String loaderVersion) {
        this.name = name;
        this.serverType = serverType;
        this.gameVersion = gameVersion;
        this.loaderVersion = loaderVersion;
    }
}
