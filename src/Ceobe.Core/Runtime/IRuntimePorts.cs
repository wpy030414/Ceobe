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
/// 服务端安装结果（runtime 本地形态，等价 <c>Providers.InstalledServer</c>）。
/// 刻意不复用 Providers 的类型：那会让 runtime → providers 产生编译期依赖，
/// 破坏「provider 可独立替换」的 SPI 边界（ADR-004）。装配方负责把两者对接。
/// </summary>
/// <param name="LaunchJar">相对实例目录的启动 jar（NeoForge 情形与 <paramref name="LaunchArgsFile"/> 同值）。</param>
/// <param name="GameVersion">provider 实际安装的游戏版本（可能与请求值不同）。</param>
/// <param name="LoaderVersion">实际选定的 loader 版本；可空。</param>
/// <param name="LaunchArgsFile">非空时改用参数文件启动（NeoForge）：<c>java @&lt;file&gt; nogui</c>。</param>
public sealed record JarInstallResult(string LaunchJar, string GameVersion, string? LoaderVersion,
    string? LaunchArgsFile);

/// <summary>
/// 把「安装服务端 jar」这件事倒置成委托，解耦 runtime ↔ providers
/// （依赖方向由装配方决定：CLI/GUI 把 ProviderRegistry 接进来）。
/// 必须带 <paramref name="serverType"/>：provider 的选择依据就是它，
/// 少了这个参数 CreateAsync 无法表达「按类型装」，会退化成只能装一种服务端。
/// 返回完整 <see cref="JarInstallResult"/> 而非裸 jar 名：NeoForge 的参数文件启动
/// （ADR-010）与 provider 校正后的版本号都要回写进 instance.json，丢字段会导致启动方式错误。
/// </summary>
public delegate Task<JarInstallResult> JarInstaller(Model.ServerType serverType, string instanceDir,
    string gameVersion, string? loaderVersion, Util.IProgressSink progress, CancellationToken ct);
