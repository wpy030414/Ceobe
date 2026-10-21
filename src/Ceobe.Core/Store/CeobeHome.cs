using Ceobe.Core.Util;

namespace Ceobe.Core.Store;

/// <summary>
/// ceobe 主目录：<c>%USERPROFILE%\.ceobe</c>（整改项 3：可由 AppSettings.HomePath / 环境变量改）。
/// 解析优先级：环境变量 <c>CEOBE_HOME</c> &gt; AppSettings.HomePath &gt; 默认。
/// Java 版另有系统属性 <c>ceobe.home</c> 一层，C# 进程无属性体系，由前两者覆盖（测试用 CEOBE_HOME）。
/// </summary>
public static class CeobeHome
{
    /// <summary>
    /// 显式覆盖（进程内）。GUI/CLI 启动时按 AppSettings 写入一次，使后续所有路径解析生效。
    /// 为 null 时回落 <c>CEOBE_HOME</c> 环境变量。
    /// </summary>
    public static string? Override { get; set; }

    public static string Root()
    {
        var explicitHome = Override ?? Environment.GetEnvironmentVariable("CEOBE_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome)) return System.IO.Path.GetFullPath(explicitHome);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            profile = Environment.GetEnvironmentVariable("USERPROFILE")
                      ?? Environment.GetEnvironmentVariable("HOME");
        }
        if (string.IsNullOrEmpty(profile)) throw new CeobeException("无法定位用户主目录");
        return System.IO.Path.Combine(profile, ".ceobe");
    }

    public static string InstancesDir() => Ensure(System.IO.Path.Combine(Root(), "instances"));

    public static string InstanceDir(string name) => System.IO.Path.Combine(InstancesDir(), name);

    public static string JarCache() => Ensure(System.IO.Path.Combine(Root(), "cache", "jars"));

    public static string Ensure(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CeobeException($"无法创建目录: {dir}", e);
        }
    }
}
