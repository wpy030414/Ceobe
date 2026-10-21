namespace Ceobe.Core.Jdk;

/// <summary>
/// 为某实例解析应当使用的 javaHome，并给出是否需要提醒用户（一键装 / 手动指定）。
/// <para>
/// C# 版决策链（ADR-019：「继承当前 JVM」退路消失后的替代链）：
/// 显式指定 &gt; 自管 JDK 命中（~/.ceobe/jdk）&gt; 宿主 JAVA_HOME（须带启动器）&gt; 失败并给出可执行下一步。
/// Java 版在「显式」与「自管」之间还有一档「当前 JVM（java.home）且带启动器 → 继承」，C# 进程不是
/// Java 进程，**没有当前 JVM 可继承**，该档整体移除——宿主 JDK 只能来自 JAVA_HOME 环境变量。
/// </para>
/// <para>
/// ADR-018 守卫：任何候选（含显式指定）都必须通过 <see cref="JavaEnv.HasLauncher"/> 才算可用；
/// 失败消息（<see cref="Resolution.Message"/>）必须包含可执行下一步，不得只说「启动失败」。
/// </para>
/// </summary>
public static class JavaResolver
{
    /// <summary>
    /// 解析结果。
    /// </summary>
    /// <param name="Required">该 MC 版本所需的 Java 主版本。</param>
    /// <param name="Actual">实际将使用的 Java 主版本（-1 = 未知）。</param>
    /// <param name="JavaHome">建议的 javaHome。
    /// <c>ok=true</c> 时**必为非空具体路径**（C# 版不存在「null = 继承当前 JVM」的语义）；
    /// <c>ok=false</c> 时为 null，调用方（Runtime/InstanceManager）必须展示 <see cref="Message"/> 而非硬启动。</param>
    /// <param name="Ok">是否满足。</param>
    /// <param name="Message">需要提醒用户的说明（含可执行下一步）；无提醒时可为 null。</param>
    public sealed record Resolution(
        int Required,
        int Actual,
        string? JavaHome,
        bool Ok,
        string? Message);

    /// <summary>
    /// 异步解析：required 经 piston-meta 查询（网络）。CLI/GUI 启动路径用这个。
    /// </summary>
    /// <param name="gameVersion">MC 游戏版本。</param>
    /// <param name="explicitJavaHome">用户显式指定的 JRE 目录（可空 = 不指定）。</param>
    public static async Task<Resolution> ResolveAsync(
        string gameVersion, string? explicitJavaHome = null, CancellationToken ct = default)
    {
        int required = await JavaRequirement.RequiredMajorAsync(gameVersion, ct).ConfigureAwait(false);
        return Resolve(gameVersion, required, explicitJavaHome, ct);
    }

    /// <summary>
    /// 同步解析（required 已知，例如 InstanceMeta 已持久化时，避免再发网络请求）。
    /// 探测（扫目录、跑 java -version）允许同步——迁移约束 6。
    /// </summary>
    public static Resolution Resolve(
        string gameVersion, int required, string? explicitJavaHome, CancellationToken ct = default)
    {
        // ① 显式指定：仍要过 ADR-018 守卫 + 主版本匹配，失败给出可执行下一步
        if (!string.IsNullOrWhiteSpace(explicitJavaHome))
        {
            bool hasLauncher = JavaEnv.HasLauncher(explicitJavaHome);
            int actual = hasLauncher ? JavaEnv.MajorOf(explicitJavaHome, ct) ?? -1 : -1;
            bool ok = hasLauncher && actual == required;
            string? message = ok ? null
                : !hasLauncher
                    ? $"指定的 JRE 缺少 java 启动器（bin/{JavaEnv.JavaExe}）: {explicitJavaHome}\n" +
                      "下一步：该目录不可用于启动服务端（ADR-018），请换完整 JDK，或留空由 ceobe 自动解析。"
                    : $"指定的 JRE 为 Java {(actual < 0 ? "未知" : actual.ToString())}，但 {gameVersion} 需要 Java {required}\n" +
                      $"下一步：`ceobe jdk install {required}`，或改指正确的 --java-home。";
            return new Resolution(required, actual, ok ? explicitJavaHome : null, ok, message);
        }

        // ② 自管 JDK 命中（~/.ceobe/jdk/<n>/，ADR-011；已入列的目录必然带启动器，HasLauncher 是 JdkStore 扫描前提）
        var installed = JdkStore.FindMajor(required, ct);
        if (installed is not null)
        {
            return new Resolution(required, required, installed, true,
                $"自动选用 ceobe 自管 JDK（Java {required}）: {installed}");
        }

        // ③ 宿主 JAVA_HOME（仅带启动器者；C# 版无「当前 JVM」档——ADR-019）
        var host = JavaEnv.HostJavaHome();
        if (host is not null)
        {
            int hostMajor = JavaEnv.MajorOf(host, ct) ?? -1;
            if (hostMajor == required)
            {
                return new Resolution(required, required, host, true, null);
            }
            // ④ 全不满足：找不到「任何」可用 JDK——失败可见化的重点文案
            return NotFound(required, hostMajor, gameVersion);
        }
        return NotFound(required, -1, gameVersion);
    }

    /// <summary>「找不到任何可用 JDK」的统一文案：列出现状 + 两条可执行下一步。</summary>
    private static Resolution NotFound(int required, int actual, string gameVersion)
    {
        string actualText = actual < 0 ? "无（未发现带 java 启动器的运行时）" : actual.ToString();
        string hostNote = actual < 0
            ? "未检测到 JAVA_HOME 指向的完整 JDK；C# 版 ceobe 不自带 JVM，必须由外部 JDK 运行服务端。"
            : $"JAVA_HOME 指向 Java {actual}，与需求不符。";
        return new Resolution(required, actual, null, false,
            $"{gameVersion} 需要 Java {required}，当前可用 Java {actualText}。\n{hostNote}\n" +
            $"下一步（二选一）：1) `ceobe jdk install {required}` 一键安装；" +
            "2) 手动指定 `--java-home <完整 JDK 目录>`。");
    }
}
