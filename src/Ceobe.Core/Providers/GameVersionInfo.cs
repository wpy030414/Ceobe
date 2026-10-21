namespace Ceobe.Core.Providers;

/// <summary>
/// 正式版游戏版本 + 发布日期（Mojang piston-meta manifest 的 <c>releaseTime</c>）。
/// 供 GUI 的新建实例版本下拉展示；<see cref="ReleaseTime"/> 为 null 表示清单里缺该字段。
/// </summary>
public sealed record GameVersionInfo(string Id, DateTimeOffset? ReleaseTime);
