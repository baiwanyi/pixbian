/**
 * 分类与分类规则的仓储抽象（M5）。
 * 职责：声明分类实体的增删改查与规则的持久化契约，供规则引擎与管理界面消费。
 * 复用约定：实现位于 Pixbian.Data，全部使用参数化查询；依赖方向 Data → Core。
 * 关键约束：新增规则前必须先经 CategoryRuleHelper.ValidatePattern 校验，
 *          仓储层不做校验（校验属业务规则），但调用方不得绕过；
 *          调整优先级后必须整体重排，避免留下重复序号导致匹配顺序不确定。
 */

using Pixbian.Core.Models;
using Pixbian.Core.Services;

namespace Pixbian.Core.Abstractions;

/// <summary>分类仓储。</summary>
public interface ICategoryRepository
{
    /// <summary>获取全部分类，按排序序号升序。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>新增分类；名称已存在时返回既有记录。</summary>
    /// <param name="name">分类名称。</param>
    /// <param name="color">展示颜色；可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<Category> AddAsync(
        string name,
        string? color = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除分类；其下规则由数据库外键级联删除。</summary>
    /// <param name="id">分类主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>分类规则仓储。</summary>
public interface ICategoryRuleRepository
{
    /// <summary>获取全部规则，按优先级降序。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<CategoryRule>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>获取全部已启用规则，按优先级降序。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<CategoryRule>> GetEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>新增规则。</summary>
    /// <param name="rule">待新增规则；Id 忽略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CategoryRule> AddAsync(CategoryRule rule, CancellationToken cancellationToken = default);

    /// <summary>更新规则。</summary>
    /// <param name="rule">待更新规则。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpdateAsync(CategoryRule rule, CancellationToken cancellationToken = default);

    /// <summary>删除规则。</summary>
    /// <param name="id">规则主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>启用或禁用规则。</summary>
    /// <param name="id">规则主键。</param>
    /// <param name="isEnabled">是否启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>按指定顺序批量更新优先级。</summary>
    /// <param name="orderedRuleIds">按期望顺序排列的规则主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpdatePrioritiesAsync(
        IReadOnlyList<long> orderedRuleIds,
        CancellationToken cancellationToken = default);

    /// <summary>批量写入条目的命中分类。</summary>
    /// <param name="matches">匹配结果集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ApplyMatchesAsync(
        IReadOnlyList<RuleMatchResult> matches,
        CancellationToken cancellationToken = default);
}
