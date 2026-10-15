package im.xrl.ceobe.core.mods;

import java.time.Instant;

/** 已安装 mod 的记录（存于 instance.json）。 */
public final class ModEntry {
    public String projectId;
    public String slug;
    public String title;
    public String versionNumber;
    public String fileName;
    public String sha1;
    /** required / optional / unsupported */
    public String envServer;
    public String envClient;
    public Instant installedAt = Instant.now();

    public ModEntry() {
    }

    public ModEntry(String projectId, String slug, String title, String versionNumber,
                    String fileName, String sha1, String envServer, String envClient) {
        this.projectId = projectId;
        this.slug = slug;
        this.title = title;
        this.versionNumber = versionNumber;
        this.fileName = fileName;
        this.sha1 = sha1;
        this.envServer = envServer;
        this.envClient = envClient;
    }
}
