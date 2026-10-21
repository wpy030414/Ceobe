using Ceobe.Core.Store;
using Ceobe.Core.Util;

namespace Ceobe.Core.Jdk;

/// <summary>
/// ceobe 自管 JDK 仓库：{@code ~/.ceobe/jdk/<name>/}（每个子目录含可执行的 bin/java[.exe]）。
/// 目录名取 <c>jdk</c> 以 Java 版 JdkStore.root() 源码为权威（ADR-011「自管 JDK 落在 ~/.ceobe/jdk/」），
/// 保证闸门 B 双跑对照时两个实现读写同一批目录。
/// </summary>
public static class JdkStore
{
    /// <summary>已安装的自管 JDK（major 解析失败时不入列）。</summary>
    /// <param name="Major">Java 主版本。</param>
    /// <param name="Name">目录名（相对仓库根）。</param>
    /// <param name="Home">绝对路径。</param>
    public sealed record InstalledJdk(int Major, string Name, string Home);

    /// <summary>仓库根目录（不存在则创建）。</summary>
    public static string Root() => CeobeHome.Ensure(Path.Combine(CeobeHome.Root(), "jdk"));

    /// <summary>扫描已安装 JDK，按主版本降序。无启动器或版本解析失败的目录被过滤。</summary>
    public static IReadOnlyList<InstalledJdk> List(CancellationToken ct = default)
    {
        var outList = new List<InstalledJdk>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root()))
            {
                ct.ThrowIfCancellationRequested();
                if (!JavaEnv.HasLauncher(dir)) continue;
                var major = JavaEnv.MajorOf(dir, ct);
                if (major is int m && m > 0)
                {
                    outList.Add(new InstalledJdk(m, Path.GetFileName(dir), dir));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (CeobeException) { throw; }
        catch (Exception)
        {
            // 扫描目录失败（权限等）：对齐 Java 版 ignored 语义，返回已发现部分
        }
        outList.Sort((a, b) => b.Major.CompareTo(a.Major));
        return outList;
    }

    /// <summary>找主版本匹配的自管 JDK；无则 null。</summary>
    public static string? FindMajor(int major, CancellationToken ct = default)
    {
        foreach (var j in List(ct))
        {
            if (j.Major == major) return j.Home;
        }
        return null;
    }
}
