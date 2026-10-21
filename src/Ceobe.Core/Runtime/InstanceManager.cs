using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ceobe.Core.Config;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Runtime;

/// <summary>
/// 实例管理器：CRUD + 生命周期 + 跨进程互斥（实现 <see cref="IInstanceHost"/> 供 tunnel 依赖）。
/// 状态存储：实例目录下的 instance.json / .pid / .state / .ctl（无中央索引，ADR-005/007）。
/// 语义照搬 Java 版 <c>runtime/InstanceManager</c>。
///
/// 与 Java 版的依赖形态差异（语义不变）：
/// - provider 安装、JDK 解析、隧道管理全部注入（<see cref="JarInstaller"/>/<see cref="IJavaLocator"/>/<see cref="IInstanceTunnels"/>），
///   core 内部依赖保持单向；
/// - 创建流程为异步（下载 jar 必然阻塞），查询/停止等保持同步以贴合 CLI 用法。
/// </summary>
public sealed partial class InstanceManager : IInstanceHost
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(90);
    private static readonly Regex NamePattern = new("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ServerProcess> _hosted = new(StringComparer.Ordinal);
    private readonly object _hostedLock = new();
    private readonly IJavaLocator _java;
    private readonly JarInstaller? _installJar;

    public InstanceManager(IJavaLocator java, JarInstaller? installJar = null)
    {
        _java = java;
        _installJar = installJar;
    }

    /// <summary>事件总线：GUI/CLI/Web 订阅同一套（对齐 Java 版 listeners）。</summary>
    public event Action<IInstanceEvent>? OnEvent;

    // —— 查询 ——

    public List<InstanceStatus> List()
    {
        var outList = LoadAllMetas().Select(statusOf).ToList();
        outList.Sort((a, b) => string.CompareOrdinal(a.Meta.Name, b.Meta.Name));
        return outList;
    }

    public InstanceStatus? Find(string name)
    {
        var dir = CeobeHome.InstanceDir(name);
        if (!File.Exists(Path.Combine(dir, "instance.json"))) return null;
        return statusOf(ReadMeta(dir));
    }

    public InstanceStatus Require(string name) =>
        Find(name) ?? throw CeobeException.Of("实例不存在: {0}", name);

    public InstanceMeta Meta(string name) => Require(name).Meta;

    public string Dir(string name) => CeobeHome.InstanceDir(name);

    // —— 端口排他性（ADR-009）——

    /// <summary>
    /// 校验端口在实例间排他：不得与其它实例已登记的端口相同。
    /// 穿透通道直接指向该端口，若两实例共用端口，玩家的地址会连到错误的服务端。
    /// </summary>
    public void AssertPortExclusive(string self, int port)
    {
        foreach (var m in LoadAllMetas())
        {
            if (m.Name != self && m.Port == port)
            {
                throw CeobeException.Of(
                    "端口 {0} 已被实例 {1} 占用；端口需在实例间排他。可改用 `--port auto` 自动选取空闲端口",
                    port, m.Name);
            }
        }
    }

    /// <summary>从 <paramref name="from"/> 起找未被任何实例登记、且本机可独占绑定的空闲端口。</summary>
    public int SuggestFreePort(int from)
    {
        var taken = LoadAllMetas().Select(m => m.Port).ToHashSet();
        for (var p = Math.Max(1, from); p <= 65535; p++)
        {
            if (!taken.Contains(p) && PortProbe.IsFree(p)) return p;
        }
        throw CeobeException.Of("从端口 {0} 起找不到可用端口", from);
    }

    private InstanceStatus statusOf(InstanceMeta meta)
    {
        var dir = CeobeHome.InstanceDir(meta.Name);
        ServerProcess? hostedProcess;
        lock (_hostedLock)
        {
            _hosted.TryGetValue(meta.Name, out hostedProcess);
        }

        if (hostedProcess is not null && hostedProcess.IsAlive)
        {
            return new InstanceStatus(meta, hostedProcess.State, PidOf(dir), true);
        }

        var pid = PidOf(dir);
        var state = ReadStateFile(dir);
        if (state.IsLive() && pid > 0 && PidAlive(pid))
        {
            return new InstanceStatus(meta, state, pid, false);
        }
        // 残留状态（进程已死）——修正为停止
        if (state.IsLive()) state = InstanceState.STOPPED;
        return new InstanceStatus(meta, state, -1, false);
    }

    // —— 实例设置 ——

    /// <summary>更新实例使用的 javaHome（null = 交由 <see cref="IJavaLocator"/> 解析）。</summary>
    public void SetJavaHome(string name, string? javaHome)
    {
        Require(name);
        var dir = Dir(name);
        var meta = ReadMeta(dir);
        meta.JavaHome = javaHome;
        SaveMeta(dir, meta);
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || !NamePattern.IsMatch(name))
        {
            throw CeobeException.Of("实例名只能是小写字母/数字/_/-，长度 1-32：{0}", name);
        }
    }

    /// <summary>重命名实例（要求已停止；目录与元数据同步改名）。</summary>
    public void Rename(string oldName, string newName)
    {
        if (oldName == newName) return;
        ValidateName(newName);
        var s = Require(oldName);
        if (s.State.IsLive()) throw CeobeException.Of("实例 {0} 正在运行，请先停止", oldName);
        if (Find(newName) is not null) throw CeobeException.Of("实例已存在: {0}", newName);

        var from = Dir(oldName);
        var to = Dir(newName);
        try
        {
            Directory.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"重命名失败: {e.Message}", e);
        }
        var meta = ReadMeta(to);
        meta.Name = newName;
        SaveMeta(to, meta);
        lock (_hostedLock)
        {
            _hosted.Remove(oldName);
        }
    }

    /// <summary>
    /// 更新实例设置（要求已停止）。参数为 null 表示不改动。
    /// 端口变更同步写入 server.properties 的 server-port/query.port；
    /// 设置 authlib-injector 时自动关闭正版验证（三方鉴权需要）。
    /// </summary>
    public void UpdateInstance(string name, int? memoryMb, int? port, string? jvmArgs,
                               string? authlibInjectorUrl)
    {
        var s = Require(name);
        if (s.State.IsLive())
        {
            throw CeobeException.Of("实例 {0} 正在运行，请先停止再修改", name);
        }
        var dir = Dir(name);
        var meta = ReadMeta(dir);

        if (memoryMb is > 0) meta.MemoryMb = memoryMb.Value;
        if (jvmArgs is not null) meta.JvmArgs = jvmArgs.Trim();

        if (port is not null && port != meta.Port)
        {
            var p = port.Value;
            if (p is < 1 or > 65535) throw CeobeException.Of("端口需在 1-65535：{0}", p);
            AssertPortExclusive(name, p);
            if (!PortProbe.IsFree(p)) throw CeobeException.Of("端口 {0} 已被本机占用", p);
            meta.Port = p;
            SyncPortToProps(dir, p);
        }

        if (authlibInjectorUrl is not null)
        {
            var url = authlibInjectorUrl.Trim();
            meta.AuthlibInjectorUrl = url.Length == 0 ? null : url;
            if (url.Length > 0) SetOnlineModeFalse(dir);
        }
        SaveMeta(dir, meta);
    }

    private static void SyncPortToProps(string dir, int port)
    {
        var props = Path.Combine(dir, "server.properties");
        if (!File.Exists(props)) return;
        var f = ServerPropertiesFile.Load(props);
        if (f.Contains("server-port")) f.Set("server-port", port.ToString());
        if (f.Contains("query.port")) f.Set("query.port", port.ToString());
        f.Save();
    }

    private static void SetOnlineModeFalse(string dir)
    {
        var props = Path.Combine(dir, "server.properties");
        if (!File.Exists(props)) return;
        var f = ServerPropertiesFile.Load(props);
        if (f.Contains("online-mode")) f.Set("online-mode", "false");
        f.Save();
    }

    // —— 创建/删除 ——

    /// <summary>
    /// 创建实例：先按最终参数落基础文件，再安装服务端 jar（经注入的 <see cref="JarInstaller"/> 委托）。
    /// </summary>
    public async Task<InstanceMeta> CreateAsync(InstanceSpec spec, IProgressSink? progress = null,
                                                CancellationToken ct = default)
    {
        spec.Validate();
        var dir = CeobeHome.InstanceDir(spec.Name);
        if (File.Exists(Path.Combine(dir, "instance.json")))
        {
            throw CeobeException.Of("实例已存在: {0}", spec.Name);
        }
        // 端口排他性：先于任何下载/落盘校验，避免冲突时才失败
        AssertPortExclusive(spec.Name, spec.Port);
        if (!PortProbe.IsFree(spec.Port))
        {
            throw CeobeException.Of(
                "端口 {0} 当前已被本机其它程序占用；可改用 `--port auto` 自动选取空闲端口", spec.Port);
        }
        CeobeHome.Ensure(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("写入 eula.txt 失败", e);
        }
        WriteDefaultProperties(dir, spec.Port);

        if (_installJar is null)
        {
            throw new CeobeException("未装配服务端安装器（JarInstaller），无法创建实例");
        }
        var sink = progress ?? NullProgress.Instance;
        sink.Update($"安装服务端 {spec.ServerType} {spec.GameVersion}", 0, -1);
        var installed = await _installJar(spec.ServerType, dir, spec.GameVersion, spec.LoaderVersion, sink, ct)
            .ConfigureAwait(false);

        var meta = new InstanceMeta
        {
            Name = spec.Name,
            ServerType = spec.ServerType,
            // provider 实际安装的版本与 loader：可能与请求值不同（如「latest stable loader」），
            // 以 provider 返回为准，否则 instance.json 记的是假的
            GameVersion = installed.GameVersion,
            LoaderVersion = installed.LoaderVersion,
            MemoryMb = spec.MemoryMb,
            JvmArgs = spec.JvmArgs ?? "",
            Port = spec.Port,
            LaunchJar = installed.LaunchJar,
            // NeoForge 走参数文件启动（ADR-010），丢了这条会错误地用 -jar server.jar 拉起
            LaunchArgsFile = installed.LaunchArgsFile,
        };
        // 解析应使用的 Java：显式指定 > 自管 JDK > 宿主 JAVA_HOME（C# 版无「继承当前 JVM」退路）。
        // 先经 locator 预热 required 主版本缓存：同步的启动路径（ServerProcess.Start → ResolveLauncher）
        // 只能读缓存、不能 await。预热失败不阻断创建——ResolveLauncher 会按「不确定」给出提示。
        try
        {
            await _java.WarmRequirementCacheAsync(spec.GameVersion, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is CeobeException or IOException)
        {
            sink.Update("提示: 未能确认该版本所需 Java（" + e.Message + "），启动时将按兜底版本解析", 0, -1);
        }
        try
        {
            var launcher = _java.ResolveLauncher(spec.JavaHome, spec.GameVersion);
            meta.JavaHome = Path.GetDirectoryName(Path.GetDirectoryName(launcher));
        }
        catch (CeobeException e)
        {
            // 对齐 Java 版的「提醒而非失败」：实例仍创建成功，启动时再报，且信息可执行
            sink.Update("警告: " + e.Message, 0, -1);
            meta.JavaHome = spec.JavaHome;
        }
        SaveMeta(dir, meta);
        sink.Update("创建完成", 1, 1);
        return meta;
    }

    private static void WriteDefaultProperties(string dir, int port)
    {
        var props = Path.Combine(dir, "server.properties");
        if (File.Exists(props)) return;
        try
        {
            // 用 template 起底：先写文本，再加载改端口（与 Java 版一致）
            File.WriteAllText(props, ServerPropertiesFile.Template(), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException("生成 server.properties 失败", e);
        }
        var f = ServerPropertiesFile.Load(props);
        f.Set("server-port", port.ToString());
        f.Set("query.port", port.ToString());
        f.Save();
    }

    /// <summary>删除实例。<paramref name="purge"/>=false 只删元数据、保留世界与 mod 文件。</summary>
    public void Remove(string name, bool purge)
    {
        var s = Require(name);
        if (s.State.IsLive()) throw CeobeException.Of("实例 {0} 仍在运行，请先停止", name);
        var dir = Dir(name);
        try
        {
            if (purge)
            {
                ZipUtil.DeleteRecursively(dir);
            }
            else
            {
                foreach (var f in new[] { "instance.json", ".state", ".pid", ".lock" })
                {
                    var p = Path.Combine(dir, f);
                    if (File.Exists(p)) File.Delete(p);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"删除实例失败: {e.Message}", e);
        }
        lock (_hostedLock)
        {
            _hosted.Remove(name);
        }
    }

    // —— 生命周期 ——

    public void Start(string name)
    {
        var s = Require(name);
        AssertPortExclusive(name, s.Meta.Port);

        ServerProcess process;
        lock (_hostedLock)
        {
            if (!_hosted.TryGetValue(name, out var existing))
            {
                if (!s.HostedHere) VerifyNotRunningElsewhere(name, s);
                existing = new ServerProcess(s.Meta, Dir(name), _java);
                existing.OnEvent += Publish;
                _hosted[name] = existing;
            }
            process = existing;
        }
        if (process.IsAlive) throw CeobeException.Of("实例 {0} 已在运行", name);

        var launch = string.IsNullOrWhiteSpace(s.Meta.LaunchArgsFile) ? s.Meta.LaunchJar : s.Meta.LaunchArgsFile;
        if (!File.Exists(Path.Combine(Dir(name), launch!)))
        {
            throw CeobeException.Of("启动器 {0} 不存在，实例可能未安装完整", launch);
        }
        process.Start();
    }

    private static void VerifyNotRunningElsewhere(string name, InstanceStatus s)
    {
        if (s.State.IsLive() && s.Pid > 0)
        {
            throw CeobeException.Of(
                "实例 {0} 已被其他进程托管（pid {1}），请先在那里停止，或用 `ceobe stop {0}` 后再试",
                name, s.Pid);
        }
    }

    public void Stop(string name)
    {
        Require(name);
        ServerProcess? p;
        lock (_hostedLock)
        {
            _hosted.TryGetValue(name, out p);
        }
        if (p is not null && p.IsAlive)
        {
            p.Stop(StopGrace);
            return;
        }
        // 非本进程托管：走控制通道，其次按 pid 提示
        var dir = Dir(name);
        if (InstanceControl.IsAvailable(dir))
        {
            InstanceControl.Stop(dir);
            return;
        }
        var pid = PidOf(dir);
        if (pid > 0 && PidAlive(pid))
        {
            throw CeobeException.Of("实例 {0} 由其他进程托管（pid {1}），无法在此处停止", name, pid);
        }
        throw CeobeException.Of("实例 {0} 未在运行", name);
    }

    public void Kill(string name)
    {
        ServerProcess? p;
        lock (_hostedLock)
        {
            _hosted.TryGetValue(name, out p);
        }
        p?.Kill();
    }

    public void Restart(string name)
    {
        Stop(name);
        Start(name);
    }

    public void Exec(string name, string command)
    {
        ServerProcess? p;
        lock (_hostedLock)
        {
            _hosted.TryGetValue(name, out p);
        }
        if (p is null || !p.IsAlive)
        {
            // 跨进程：经控制通道下发（对齐 CLI 的 `ceobe exec`）
            var dir = Dir(name);
            if (InstanceControl.IsAvailable(dir))
            {
                InstanceControl.Exec(dir, command);
                return;
            }
            throw CeobeException.Of("实例 {0} 未在本进程运行，无法发送命令", name);
        }
        p.SendCommand(command);
    }

    /// <summary>本进程托管的实例（GUI 可订阅其日志）；未托管返回 null。</summary>
    public ServerProcess? ProcessOf(string name)
    {
        lock (_hostedLock)
        {
            _hosted.TryGetValue(name, out var p);
            return p;
        }
    }

    /// <summary>
    /// 为本进程托管的实例开启控制通道（监督进程用）。
    /// 隧道管理器由调用方传入而非从 manager 取——对齐 Java 版 <c>ControlServer(process, dir, name, tunnels)</c>，
    /// 避免 runtime ↔ tunnel 形成循环依赖（tunnel 依赖 IInstanceHost=manager，manager 不该反向依赖 tunnel）。
    /// </summary>
    public ControlServer OpenControl(string name, IInstanceTunnels? tunnels)
    {
        var p = ProcessOf(name) ?? throw CeobeException.Of("实例 {0} 未在本进程运行", name);
        return new ControlServer(p, Dir(name), name, tunnels);
    }

    // —— IInstanceHost ——

    /// <summary>记录/刷新某通道的公网地址并广播（Pinggy 续期后会再次调用）。</summary>
    public void RememberTunnel(string name, string kindId, string? publicUrl)
    {
        var dir = Dir(name);
        var meta = ReadMeta(dir);
        switch (kindId)
        {
            case "pinggy": meta.LastPinggyAddress = publicUrl; break;
            case "cloudflare": meta.LastCloudflareUrl = publicUrl; break;
        }
        SaveMeta(dir, meta);
        Publish(new TunnelChanged(name, kindId, publicUrl));
    }

    /// <summary>注入隧道诊断日志到实例事件流（GUI 控制台可见）。</summary>
    public void TunnelLog(string name, string line) => Publish(new InstanceLog(name, line));

    /// <summary>
    /// 测试缝：从外部派发一个实例事件。
    ///
    /// 为什么需要：<see cref="OnEvent"/> 是 public event，外部只能订阅不能触发；
    /// 而状态变化事件的真实来源是 <see cref="ServerProcess"/> 的日志泵与看门狗——
    /// 要触发它就得拉起一个真的 MC 服务端。ViewModel 的「事件到达后就地更新行」
    /// 这条逻辑（闸门 C 的重点）不可能靠真进程来测，故开这个 internal 缝。
    /// 仅供测试程序集使用，不对 GUI/CLI 暴露。
    /// </summary>
    internal void PublishForTest(IInstanceEvent evt) => Publish(evt);

    // —— 元数据 IO ——

    private List<InstanceMeta> LoadAllMetas()
    {
        var outList = new List<InstanceMeta>();
        var baseDir = CeobeHome.InstancesDir();
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(baseDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return outList;
        }
        foreach (var d in dirs)
        {
            if (!File.Exists(Path.Combine(d, "instance.json"))) continue;
            try
            {
                outList.Add(ReadMeta(d));
            }
            catch (CeobeException)
            {
                // 单个实例元数据损坏不应让整张列表失效
            }
        }
        return outList;
    }

    internal static InstanceMeta ReadMeta(string dir)
    {
        try
        {
            return JsonUtil.Parse<InstanceMeta>(
                File.ReadAllText(Path.Combine(dir, "instance.json"), new UTF8Encoding(false)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"读取实例元数据失败: {dir}", e);
        }
    }

    internal static void SaveMeta(string dir, InstanceMeta meta)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, "instance.json"), JsonUtil.Write(meta),
                new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"写入实例元数据失败: {dir}", e);
        }
    }

    internal static long PidOf(string dir)
    {
        var pidFile = Path.Combine(dir, ".pid");
        if (!File.Exists(pidFile)) return -1;
        try
        {
            return long.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) ? pid : -1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>
    /// 进程存活判定。对齐 Java 的 <c>ProcessHandle.of(pid).isAlive()</c> 语义，
    /// 但必须注意 Windows 下 PID 会被复用：<c>Process.GetProcessById</c> 只能证明「该 PID 上有进程」，
    /// 不能证明它是我们启动的那个 MC 服务端。真正的排他依据是控制通道（token 鉴权），
    /// 本方法只用于状态推断，不得用于「可以杀它」这类破坏性判断。
    /// </summary>
    internal static bool PidAlive(long pid)
    {
        if (pid <= 0) return false;
        try
        {
            var p = Process.GetProcessById((int)pid);
            var alive = !p.HasExited;
            p.Dispose();
            return alive;
        }
        catch (ArgumentException)
        {
            return false; // 不存在
        }
        catch (InvalidOperationException)
        {
            return false; // 已退出
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static InstanceState ReadStateFile(string dir)
    {
        var f = Path.Combine(dir, ".state");
        if (!File.Exists(f)) return InstanceState.CREATED;
        try
        {
            var text = File.ReadAllText(f).Trim();
            return Enum.TryParse<InstanceState>(text, out var s) ? s : InstanceState.CREATED;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return InstanceState.CREATED;
        }
    }

    private void Publish(IInstanceEvent evt)
    {
        foreach (var handler in OnEvent?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<IInstanceEvent>)handler)(evt);
            }
            catch (Exception)
            {
                // 单个订阅者异常不影响其他
            }
        }
    }
}

