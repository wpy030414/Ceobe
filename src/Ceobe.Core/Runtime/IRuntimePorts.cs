namespace Ceobe.Core.Runtime;

/// <summary>
/// 实例状态快照：元数据 + 运行态 + 归属信息。
/// 供 tunnel / webconsole 依赖，避免它们抓 <c>InstanceManager</c> 具体类。
/// </summary>
/// <param name="Meta">持久化元数据</param>
/// <param name="State">当前状态机位置</param>
/// <param name="Pid">&lt;=0 表示无已知进程</param>
/// <param name="HostedHere">进程是否由本进程托管</param>
public sealed record InstanceStatus(Model.InstanceMeta Meta, Model.InstanceState State, long Pid, bool HostedHere)
{
    public string Name => Meta.Name;
    public int Port => Meta.Port;
}

/// <summary>
/// 实例运行态的窄视图，供 tunnel 等非运行时模块依赖（依赖方向恒为 tunnel → runtime）。
/// <c>InstanceManager</c> 实现它。
/// </summary>
public interface IInstanceHost
{
    /// <summary>实例当前状态；不存在则抛 <see cref="Util.CeobeException"/>。</summary>
    InstanceStatus Require(string name);

    /// <summary>实例目录绝对路径。</summary>
    string Dir(string name);

    /// <summary>记录某通道最近一次公网地址并广播事件（落盘 instance.json）。</summary>
    void RememberTunnel(string name, string kindId, string? publicUrl);

    /// <summary>把一行隧道日志注入该实例的事件流（GUI 控制台可见）。</summary>
    void TunnelLog(string name, string line);
}

/// <summary>
/// 定位「可用于 fork 服务端的 java 启动器」——具体实现在 <c>Ceobe.Core.Jdk</c>，
/// Runtime 只认契约，避免 runtime ↔ jdk 编译期环。
/// </summary>
public interface IJavaLocator
{
    /// <summary>
    /// 决策链：显式 javaHome &gt; 自管 JDK &gt; 宿主 JAVA_HOME；落空则抛含下一步建议的异常。
    /// 传入游戏版本而非主版本号：「哪个 MC 版本要哪个 Java」属于 JDK 层的知识
    /// （见 JavaRequirement / ADR-006），Runtime 不应复制这张表，否则两处会漂移。
    /// </summary>
    string ResolveLauncher(string? javaHome, string gameVersion);
}

/// <summary>
/// 把「安装服务端 jar」这件事倒置成委托，解耦 runtime ↔ providers。
/// 返回实际用于启动的 jar 文件名（相对实例目录）。
/// </summary>
public delegate Task<string> JarInstaller(string instanceDir, string gameVersion, string? loaderVersion,
    Util.IProgressSink progress, CancellationToken ct);
