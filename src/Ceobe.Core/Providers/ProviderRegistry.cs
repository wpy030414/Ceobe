using System.Collections.Concurrent;
using Ceobe.Core.Model;
using Ceobe.Core.Util;

namespace Ceobe.Core.Providers;

/// <summary>
/// provider 注册表：类型 → 实现。对齐 Java <c>ProviderRegistry</c>（EnumMap → ConcurrentDictionary，
/// 注册发生在静态构造，读取可能来自 GUI 后台线程，故用并发安全容器）。
/// </summary>
public static class ProviderRegistry
{
    private static readonly ConcurrentDictionary<ServerType, IServerJarProvider> Providers = new();

    static ProviderRegistry()
    {
        Register(new MojangProvider());
        Register(new PaperProvider());
        Register(new FabricProvider());
        Register(new NeoForgeProvider());
        Register(new PurpurProvider());
    }

    public static void Register(IServerJarProvider provider) => Providers[provider.Type] = provider;

    public static IServerJarProvider Of(ServerType type)
    {
        if (Providers.TryGetValue(type, out var p)) return p;
        throw CeobeException.Of("暂不支持的服务端类型: {0}", type);
    }
}
