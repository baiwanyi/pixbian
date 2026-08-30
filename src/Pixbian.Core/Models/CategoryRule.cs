/**
 * 分类与分类规则模型（M5）。
 * 职责：描述「分类」实体与「正则匹配规则」，供规则引擎按文件名自动归类。
 * 复用约定：Pattern 一律经 Regex 编译并强制设置匹配超时，杜绝灾难性回溯；
 *          规则命中采用「首个命中即停止」的短路策略，优先级数值越大越先匹配。
 * 关键约束：Pattern 来自用户输入，属不可信数据，必须经 ValidatePattern 校验后才允许入库，
 *          否则一个会回溯的正则就能把索引线程卡死（ReDoS）；
 *          分类被删除时其下规则须级联删除，数据库已通过外键 ON DELETE CASCADE 保证。
 */

using System.Text.RegularExpressions;

namespace Pixbian.Core.Models;

/// <summary>分类。</summary>
public sealed record Category
{
    /// <summary>数据库主键；未入库为 0。</summary>
    public long Id { get; init; }

    /// <summary>分类名称，唯一。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>展示颜色（十六进制，如 #FF8800）；为空时使用主题默认色。</summary>
    public string? Color { get; init; }

    /// <summary>排序序号，升序排列。</summary>
    public int SortOrder { get; init; }
}

/// <summary>规则的匹配目标。</summary>
public enum RuleMatchTarget
{
    /// <summary>文件名（含扩展名）。</summary>
    FileName = 0,

    /// <summary>完整路径。</summary>
    FullPath = 1,

    /// <summary>扩展名（含点号）。</summary>
    Extension = 2
}

/// <summary>正则分类规则。</summary>
public sealed record CategoryRule
{
    /// <summary>数据库主键；未入库为 0。</summary>
    public long Id { get; init; }

    /// <summary>规则名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>正则表达式。</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>命中后归入的分类主键。</summary>
    public long CategoryId { get; init; }

    /// <summary>匹配目标。</summary>
    public RuleMatchTarget Target { get; init; } = RuleMatchTarget.FileName;

    /// <summary>是否启用；禁用后不参与匹配。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>优先级，数值越大越先匹配。</summary>
    public int Priority { get; init; }

    /// <summary>是否区分大小写。</summary>
    public bool IsCaseSensitive { get; init; }

    /// <summary>规则校验结果。</summary>
    /// <param name="IsValid">正则是否合法且安全。</param>
    /// <param name="ErrorMessage">校验失败的原因；成功时为空。</param>
    public sealed record ValidationResult(bool IsValid, string ErrorMessage)
    {
        /// <summary>校验通过的结果。</summary>
        public static ValidationResult Success { get; } = new(true, string.Empty);
    }
}

/// <summary>正则规则的工具方法。</summary>
public static class CategoryRuleHelper
{
    /// <summary>单条匹配的超时上限；超时即判定为存在灾难性回溯风险。</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>规则名称的最大长度。</summary>
    public const int MaxNameLength = 100;

    /// <summary>正则表达式的最大长度；超长模式难以审计且易触发回溯。</summary>
    public const int MaxPatternLength = 500;

    /// <summary>校验正则是否可安全使用。</summary>
    /// <param name="pattern">待校验的正则表达式。</param>
    /// <returns>校验结果。</returns>
    public static CategoryRule.ValidationResult ValidatePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new CategoryRule.ValidationResult(false, "正则表达式不能为空。");
        }

        if (pattern.Length > MaxPatternLength)
        {
            return new CategoryRule.ValidationResult(
                false,
                $"正则表达式过长（上限 {MaxPatternLength} 个字符）。");
        }

        try
        {
            // 编译期试构造：非法语法在此抛出；MatchTimeout 保证运行期不会无限回溯。
            _ = new Regex(pattern, RegexOptions.None, MatchTimeout);
            return CategoryRule.ValidationResult.Success;
        }
        catch (RegexMatchTimeoutException)
        {
            return new CategoryRule.ValidationResult(false, "正则表达式过于复杂，存在性能风险。");
        }
        catch (ArgumentException ex)
        {
            return new CategoryRule.ValidationResult(false, $"正则表达式无效：{ex.Message}");
        }
    }

    /// <summary>构造用于匹配的正则实例。</summary>
    /// <param name="rule">分类规则。</param>
    /// <returns>已配置超时与大小写选项的正则。</returns>
    /// <exception cref="ArgumentException">正则无效时抛出。</exception>
    public static Regex BuildRegex(CategoryRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var options = rule.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        // 不得使用 RegexOptions.Compiled：反射发出生成的程序集无法卸载，
        // 在用户反复编辑规则时会持续累积内存。
        return new Regex(rule.Pattern, options, MatchTimeout);
    }

    /// <summary>取出规则应当匹配的文本。</summary>
    /// <param name="target">匹配目标。</param>
    /// <param name="fileName">文件名。</param>
    /// <param name="fullPath">完整路径。</param>
    /// <returns>待匹配的字符串。</returns>
    public static string ExtractTargetText(RuleMatchTarget target, string fileName, string fullPath) =>
        target switch
        {
            RuleMatchTarget.FullPath => fullPath,
            RuleMatchTarget.Extension => Path.GetExtension(fileName),
            _ => fileName
        };
}
