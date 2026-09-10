/**
 * 用户数据备份服务抽象（导出 / 导入）。
 * 职责：把收藏与评分、收藏分组、分类与规则、扫描源等用户数据导出为单个 JSON 数据包，
 *      并按「合并、导入文件为准」的策略导入回索引库。
 * 复用约定：条目以 media_items.path 匹配（大小写不敏感），分类与分组按名称合并；
 *          实现位于 Pixbian.Data，依赖方向 Data → Core。
 * 关键约束：导入必须整体落在**单个事务**内，任一步失败即回滚，杜绝「导入一半」的脏状态；
 *          导入前必须校验格式版本，且每条规则的正则都要经 CategoryRuleHelper.ValidatePattern
 *          校验（损坏或恶意备份不得把灾难性回溯的正则灌进库）；
 *          同一文件重复导入结果一致（按业务键去重，幂等）；
 *          备份文件不含任何凭据，实现方不得向其中写入密码哈希一类敏感字段。
 */

using Pixbian.Core.Models;

namespace Pixbian.Core.Abstractions;

/// <summary>用户数据备份服务。</summary>
public interface IUserDataBackupService
{
    /// <summary>把索引库中的用户数据导出为 JSON 数据包。</summary>
    /// <param name="destinationPath">目标文件路径；写入采用「临时文件 + 原子替换」。</param>
    /// <param name="musicFolders">音乐库目录（不在索引库中，由调用方从设置提供）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出内容的规模统计。</returns>
    Task<UserDataBackupCounts> ExportAsync(
        string destinationPath,
        IReadOnlyList<string> musicFolders,
        CancellationToken cancellationToken = default);

    /// <summary>把备份文件合并导入索引库；失败抛异常且库保持原状。</summary>
    /// <param name="sourcePath">备份文件路径。</param>
    /// <param name="options">导入选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入结果统计与未匹配路径清单。</returns>
    Task<UserDataImportResult> ImportAsync(
        string sourcePath,
        UserDataImportOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>导入选项。</summary>
public sealed record UserDataImportOptions
{
    /// <summary>是否应用条目的分类归属；关闭时只导入分类与规则本身。</summary>
    /// <remarks>规则「重新匹配」会覆盖已写入的归属，故该项默认开启但可关闭。</remarks>
    public bool ImportItemCategories { get; init; } = true;

    /// <summary>路径前缀重映射（旧根 → 新根），用于换盘或换根目录后匹配。</summary>
    public IReadOnlyList<PathPrefixMapping> PathMappings { get; init; } = [];
}

/// <summary>路径前缀映射：把备份中以 <see cref="From"/> 开头的路径改写为 <see cref="To"/> 开头。</summary>
/// <param name="From">备份中的旧前缀（如 D:\Downloads）。</param>
/// <param name="To">本机的目标前缀（如 E:\Media）。</param>
public sealed record PathPrefixMapping(string From, string To);

/// <summary>导入结果。</summary>
public sealed record UserDataImportResult
{
    /// <summary>新建的分类 / 分组 / 规则数。</summary>
    public int Added { get; init; }

    /// <summary>更新的分类 / 分组 / 规则数。</summary>
    public int Updated { get; init; }

    /// <summary>成功写入或更新的收藏条目数。</summary>
    public int Favorites { get; init; }

    /// <summary>未匹配到索引记录的路径数（文件已被移动或删除）。</summary>
    public int Unmatched { get; init; }

    /// <summary>因校验不通过而跳过的规则数（如正则存在灾难性回溯风险）。</summary>
    public int SkippedRules { get; init; }

    /// <summary>未匹配的路径清单，供界面展示或导出报告。</summary>
    public IReadOnlyList<string> UnmatchedPaths { get; init; } = [];

    /// <summary>备份文件中的音乐库目录。</summary>
    /// <remarks>
    /// 音乐目录存在设置文件而非索引库，服务层不写入设置（避免数据层依赖设置服务），
    /// 由调用方与本地列表合并后落盘。
    /// </remarks>
    public IReadOnlyList<string> MusicFolders { get; init; } = [];
}
