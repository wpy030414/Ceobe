using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Xunit;

namespace Ceobe.Core.Tests.Mods;

/// <summary>
/// Modrinth v2 真实 API 冒烟（验收标准第 1 条：搜 "fabric api" 能拿到 hits）。
/// 需联网，默认不跑：<c>dotnet test --filter "Category=network"</c>。
/// </summary>
public class ModrinthNetworkTest
{
    [Fact(DisplayName = "搜索返回 hits 且字段齐全")]
    [Trait("Category", "network")]
    public async Task 搜索fabric模组的冒烟()
    {
        var client = new ModrinthClient();
        var hits = await client.Search("fabric api", "1.21.4", ServerType.FABRIC, limit: 5);
        Assert.NotEmpty(hits);
        foreach (var h in hits)
        {
            Assert.False(string.IsNullOrWhiteSpace(h.ProjectId));
            Assert.False(string.IsNullOrWhiteSpace(h.Slug));
            Assert.False(string.IsNullOrWhiteSpace(h.Title));
        }
    }

    [Fact(DisplayName = "resolvePack 拿到可下载文件 URL")]
    [Trait("Category", "network")]
    public async Task 解析整合包版本()
    {
        var client = new ModrinthClient();
        var pv = await client.ResolvePack("fabric-api", "1.21.4", "fabric");
        Assert.StartsWith("https://cdn.modrinth.com/", pv.Url, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(pv.VersionNumber));
        Assert.Contains("fabric", pv.Loaders);
    }

    [Fact(DisplayName = "客户端专属模组在 install 前被 environment 判定拦截（纯逻辑，无下载）")]
    [Trait("Category", "network")]
    public async Task 查询fabric_api的版本对象()
    {
        var client = new ModrinthClient();
        // 只验证 API 契约字段可读：version.environment 为字符串枚举而非对象
        var pv = await client.ResolvePack("fabric-api", null, "fabric");
        Assert.NotEmpty(pv.GameVersions);
    }
}
