using Ceobe.Core.Store;
using Xunit;

namespace Ceobe.Core.Tests.Jdk;

/// <summary>
/// CeobeHome.Override 是进程级静态：Jdk 包三组测试都会改写它，xUnit 默认并行会互相串台
/// （实测 JdkStore 扫描被并发 Dispose 重置后扫到真实 ~/.ceobe/jdk）。
/// 放进同一个 collection 串行执行，只影响 Jdk 测试，不污染其他包测试的并行度。
/// </summary>
[CollectionDefinition("Jdk statics")]
public class JdkCollection;

/// <summary>串行基类：所有触碰静态 Override 的 Jdk 测试类继承它。</summary>
[Collection("Jdk statics")]
public abstract class JdkSerialTestBase : IDisposable
{
    protected readonly string Home =
        Path.Combine(Path.GetTempPath(), "ceobe-jdk-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome = CeobeHome.Override;

    protected JdkSerialTestBase()
    {
        Directory.CreateDirectory(Home);
        CeobeHome.Override = Home; // 必须生效，否则测试会读写真实的 ~/.ceobe/jdk
    }

    public virtual void Dispose()
    {
        CeobeHome.Override = _savedHome;
        try { Directory.Delete(Home, recursive: true); } catch (IOException) { }
    }
}
