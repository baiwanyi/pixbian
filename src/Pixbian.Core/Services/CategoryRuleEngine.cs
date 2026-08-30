/**
 * 正则分类规则引擎（M5）。
 * 职责：按优先级顺序用已启用规则匹配媒体条目，命中即归入对应分类。
 * 复用约定：规则在每次批量匹配前编译一次并复用；匹配顺序为优先级降序、编号升序，保证结果稳定。
 * 关键约束：ReDoS 防护为强制要求，四道防线缺一不可——
 *          ① 正则构造时强制 MatchTimeout；② 超时捕获后判定该规则为危险并跳过；
 *          ③ 整批匹配设有总时限，超时即中断；④ 待匹配文本长度截断，不喂入超长输入。
 *          任一环节缺失，用户一条恶意正则就足以把索引线程拖死。
 */

using System.Diagnostics;
using System.Text.RegularExpressions;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>单条规则的匹配结果。</summary>
/// <param name="MediaId">媒体条目主键。</param>
/// <param name="CategoryId">命中的分类主键；未命中为 null。</param>
/// <param name="MatchedRuleId">命中的规则主键；未命中为 null。</param>
public sealed record RuleMatchResult(long MediaId, long? CategoryId, long? MatchedRuleId);

/// <summary>批量匹配的汇总报告。</summary>
/// <param name="ProcessedCount">已处理条目数。</param>
/// <param name="MatchedCount">命中规则的条目数。</param>
/// <param name="DangerousRuleCount">因超时被判定为危险而跳过的规则数。</param>
/// <param name="WasCancelled">是否因超过总时限而提前中断。</param>
public sealed record RuleMatchReport(
    int ProcessedCount,
    int MatchedCount,
    int DangerousRuleCount,
    bool WasCancelled);

/// <summary>正则分类规则引擎。</summary>
public sealed class CategoryRuleEngine
{
    private const int MaxInputLength = 4096;

    private static readonly TimeSpan DefaultBatchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>按规则集匹配单个条目。</summary>
    /// <param name="item">媒体条目。</param>
    /// <param name="compiledRules">已按优先级排序并编译好的规则。</param>
    /// <returns>匹配结果。</returns>
    public static RuleMatchResult Match(MediaItem item, IReadOnlyList<CompiledRule> compiledRules)
    {
        ArgumentNullException.ThrowIfNull(item);

        foreach (var compiled in compiledRules)
        {
            var text = CategoryRuleHelper.ExtractTargetText(
                compiled.Rule.Target,
                item.FileName,
                item.Path);

            if (text.Length > MaxInputLength)
            {
                text = text[..MaxInputLength];
            }

            try
            {
                // 首个命中即短路返回，避免对全部规则做无谓匹配。
                if (compiled.Regex.IsMatch(text))
                {
                    return new RuleMatchResult(item.Id, compiled.Rule.CategoryId, compiled.Rule.Id);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 该规则存在灾难性回溯风险，跳过它继续尝试后续规则。
                compiled.IsDangerous = true;
            }
        }

        return new RuleMatchResult(item.Id, null, null);
    }

    /// <summary>批量匹配条目。</summary>
    /// <param name="items">待匹配的条目。</param>
    /// <param name="rules">规则集合；引擎内部会筛选已启用项并排序。</param>
    /// <param name="batchTimeout">整批匹配的总时限；为空时使用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配报告与逐条结果。</returns>
    public static (RuleMatchReport Report, IReadOnlyList<RuleMatchResult> Results) MatchBatch(
        IReadOnlyList<MediaItem> items,
        IReadOnlyList<CategoryRule> rules,
        TimeSpan? batchTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rules);

        var compiledRules = CompileRules(rules);

        var results = new List<RuleMatchResult>(items.Count);
        var matched = 0;
        var stopwatch = Stopwatch.StartNew();
        var limit = batchTimeout ?? DefaultBatchTimeout;
        var cancelled = false;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 总时限到期即中断，防止大量慢规则叠加后无限拉长任务。
            if (stopwatch.Elapsed >= limit)
            {
                cancelled = true;
                break;
            }

            var result = Match(item, compiledRules);
            results.Add(result);

            if (result.CategoryId.HasValue)
            {
                matched++;
            }
        }

        var dangerous = compiledRules.Count(r => r.IsDangerous);

        return (
            new RuleMatchReport(results.Count, matched, dangerous, cancelled),
            results);
    }

    /// <summary>筛选已启用规则、按优先级排序并编译。</summary>
    private static List<CompiledRule> CompileRules(IReadOnlyList<CategoryRule> rules)
    {
        var compiled = new List<CompiledRule>(rules.Count);

        foreach (var rule in rules.Where(r => r.IsEnabled)
                     .OrderByDescending(r => r.Priority)
                     .ThenBy(r => r.Id))
        {
            try
            {
                compiled.Add(new CompiledRule(rule, CategoryRuleHelper.BuildRegex(rule)));
            }
            catch (ArgumentException)
            {
                // 非法正则（例如历史遗留数据）直接跳过，不阻断整批匹配。
            }
        }

        return compiled;
    }
}

/// <summary>已编译的规则与其运行时状态。</summary>
public sealed class CompiledRule
{
    /// <summary>初始化已编译规则。</summary>
    /// <param name="rule">原始规则。</param>
    /// <param name="regex">编译好的正则。</param>
    public CompiledRule(CategoryRule rule, Regex regex)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(regex);

        Rule = rule;
        Regex = regex;
    }

    /// <summary>原始规则。</summary>
    public CategoryRule Rule { get; }

    /// <summary>编译好的正则。</summary>
    public Regex Regex { get; }

    /// <summary>是否在匹配中触发过超时，即存在灾难性回溯风险。</summary>
    public bool IsDangerous { get; internal set; }
}
