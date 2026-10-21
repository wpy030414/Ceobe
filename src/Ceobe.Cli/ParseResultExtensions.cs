using System.CommandLine;
using System.CommandLine.Parsing;
using Ceobe.Core.Util;

namespace Ceobe.Cli;

/// <summary>
/// System.CommandLine beta5 的取值助手。
///
/// 为什么需要：<c>ParseResult.GetValue(Argument&lt;string&gt;)</c> 的返回类型是 <c>string?</c>，
/// 即使该 Argument 是必填的（arity ExactlyOne，解析阶段已保证非空）。直接把可空值传给
/// 非空参数会产生大量 CS8604 警告——本项目实测 36 处，噪音会把真正的可空性 bug 埋掉。
///
/// 处理方式不是加 <c>!</c> 盲信库，而是这里做一次**显式运行时检查**：
/// 万一库的 arity 语义变化或调用方把可选 Argument 传进来，会得到一条可诊断的
/// <see cref="CeobeSpec"/>（用法错误，退出码 2）而不是 NullReferenceException。
/// </summary>
public static class ParseResultExtensions
{
    /// <summary>取必填位置参数的值；缺失时抛用法错误。</summary>
    public static string Required(this ParseResult pr, Argument<string> argument) =>
        pr.GetValue(argument)
        ?? throw new CeobeSpec($"缺少参数: {argument.Name}");

    /// <summary>
    /// 取必填位置参数的值并去掉首尾空白。
    /// CLI 输入常被 shell 或粘贴带上空格（如 <c>ceobe rm " alpha "</c>），
    /// 而实例名规则是 <c>[a-z0-9_-]{1,32}</c>，带空白会校验失败并报出令人困惑的错误。
    /// </summary>
    public static string RequiredTrimmed(this ParseResult pr, Argument<string> argument) =>
        pr.Required(argument).Trim();
}
