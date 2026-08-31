/**
 * 媒体条目与扫描源的仓储抽象（M1）。
 * 职责：为领域服务声明数据访问契约，使索引与监控逻辑不依赖具体数据库实现，便于单测与后续替换存储。
 * 复用约定：实现位于 Pixbian.Data，全部使用参数化查询；依赖方向严格为 Data → Core，本文件不得引用下层类型。
 * 关键约束：所有写操作必须批量化（单事务多语句），逐条提交在大库场景下会带来数量级的耗时差异；
 *          GetPathsUnderDirectory 的 LIKE 前缀匹配必须做通配符转义，否则含 % 或 _ 的目录名会导致对账误删。
 */

using Pixbian.Core.Models;

namespace Pixbian.Core.Abstractions;

/// <summary>媒体条目仓储。</summary>
public interface IMediaItemRepository
{
    /// <summary>批量写入或更新条目，单个事务内完成。</summary>
    /// <param name="items">待写入条目；Id 可为空，以 Path 作为冲突目标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpsertBatchAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken = default);

    /// <summary>查询指定目录及其所有子目录下已索引的文件路径。</summary>
    /// <param name="directory">目录完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已索引的文件路径集合。</returns>
    Task<IReadOnlyList<string>> GetPathsUnderDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default);

    /// <summary>按路径批量删除条目。</summary>
    /// <param name="paths">待删除的文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>按主键批量删除条目。</summary>
    /// <param name="ids">待删除条目的主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteByIdsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default);

    /// <summary>按条件分页查询条目。</summary>
    /// <param name="query">查询条件，含类型、搜索、排序与分页。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页的条目集合。</returns>
    Task<IReadOnlyList<MediaItem>> QueryAsync(
        MediaQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>按主键批量设置收藏状态。</summary>
    /// <param name="ids">条目的主键。</param>
    /// <param name="isFavorite">目标收藏状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetFavoriteAsync(
        IReadOnlyList<long> ids,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    /// <summary>按偏移量定位单条条目，用于随机抽样。</summary>
    /// <param name="kind">媒体类型；为 null 时不限制。</param>
    /// <param name="offset">跳过的条目数，须大于等于 0。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该位置的条目；越界或库为空时返回 null。</returns>
    Task<MediaItem?> GetAtOffsetAsync(
        MediaKind? kind,
        int offset,
        CancellationToken cancellationToken = default);

    /// <summary>按主键获取单条条目，用于 Web 访问与详情展示。</summary>
    /// <param name="id">条目主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目；不存在时返回 null。</returns>
    Task<MediaItem?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>统计条目数量。</summary>
    /// <param name="kind">媒体类型；为 null 时统计全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目数量。</returns>
    Task<int> CountAsync(MediaKind? kind, CancellationToken cancellationToken = default);

    /// <summary>按完整查询条件统计条目数量，用于页头展示当前内容的规模。</summary>
    /// <param name="query">查询条件；分页字段（Skip / Take）不参与计数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>符合全部条件的条目数量。</returns>
    Task<int> CountByQueryAsync(MediaQuery query, CancellationToken cancellationToken = default);
}

/// <summary>媒体库扫描源仓储。</summary>
public interface ILibraryFolderRepository
{
    /// <summary>获取全部扫描源。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<LibraryFolder>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>新增扫描源；路径已存在时直接返回既有记录。</summary>
    /// <param name="path">目录完整路径。</param>
    /// <param name="displayName">展示名称；为 null 时由目录名派生。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<LibraryFolder> AddAsync(
        string path,
        string? displayName = null,
        CancellationToken cancellationToken = default);

    /// <summary>移除扫描源。</summary>
    /// <param name="id">扫描源主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemoveAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>启用或禁用扫描源。</summary>
    /// <param name="id">扫描源主键。</param>
    /// <param name="isEnabled">是否启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>更新最近扫描时间。</summary>
    /// <param name="id">扫描源主键。</param>
    /// <param name="scannedUtc">扫描完成时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpdateLastScanAsync(
        long id,
        DateTimeOffset scannedUtc,
        CancellationToken cancellationToken = default);
}
