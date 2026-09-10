/**
 * 媒体条目、扫描源与音乐曲目的仓储抽象（M1）。
 * 职责：为领域服务声明数据访问契约，使索引、监控与元数据回填不依赖具体数据库实现，便于单测与后续替换存储。
 * 复用约定：实现位于 Pixbian.Data，全部使用参数化查询；依赖方向严格为 Data → Core，本文件不得引用下层类型。
 * 关键约束：所有写操作必须批量化（单事务多语句），逐条提交在大库场景下会带来数量级的耗时差异；
 *          目录归属判定用前缀区间比较（directory >= 前缀 AND < 前缀 + 哨兵）而非 LIKE：
 *          LIKE 默认大小写不敏感，无法走 BINARY 索引范围扫描，也无法直接处理含 % 或 _ 的目录名；
 *          对账走流式枚举（EnumeratePathsUnderDirectory）而非一次性取列表，避免大库下数百 MB 常驻；
 *          元数据写回只允许覆盖 width / height / duration_ms 三个列，用户数据一律不得触碰。
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

    /// <summary>逐个枚举指定目录及其所有子目录下已索引的文件路径。</summary>
    /// <param name="directory">目录完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件路径的异步序列。</returns>
    /// <remarks>
    /// 供对账使用：百万条规模下把全部路径一次性物化进列表需要数百 MB 常驻内存，
    /// 流式枚举让调用方边读边比对，内存占用与目录规模解耦。
    /// 需要完整列表作为批量删除入参的场景，继续使用 <see cref="GetPathsUnderDirectoryAsync"/>。
    /// </remarks>
    IAsyncEnumerable<string> EnumeratePathsUnderDirectoryAsync(
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

    /// <summary>取一批尚未完成元数据探测的条目，供后台回填按批推进。</summary>
    /// <param name="limit">本批最大条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待探测条目；为空表示回填已全部完成。</returns>
    Task<IReadOnlyList<MediaItem>> GetMetadataPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>批量写入元数据探测结果，单个事务内完成。</summary>
    /// <param name="updates">更新内容；结果为 null 表示探测失败，状态置为 Failed 且不再重试。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpdateMetadataBatchAsync(
        IReadOnlyList<MediaMetadataUpdate> updates,
        CancellationToken cancellationToken = default);
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

/// <summary>音乐曲目仓储。</summary>
public interface IMusicTrackRepository
{
    /// <summary>取回全部音乐曲目。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>库内全部曲目；尚未扫描过时为空集合。</returns>
    Task<IReadOnlyList<MusicTrack>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>以给定集合全量替换库内曲目，单个事务内完成。</summary>
    /// <param name="tracks">扫描得到的曲目集合；为空表示清空音乐库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 音乐库规模远小于图库（数百至数千条），全量替换比增量对账更简单可靠：
    /// 不必维护「已删除」状态列，磁盘上消失的文件在下一次扫描后自然不再出现。
    /// </remarks>
    Task ReplaceAllAsync(
        IReadOnlyList<MusicTrack> tracks,
        CancellationToken cancellationToken = default);
}

/// <summary>收藏分组仓储。</summary>
public interface IFavoriteGroupRepository
{
    /// <summary>获取全部分组，含仍处于收藏态的成员数量。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按排序序号与名称升序排列的分组集合。</returns>
    Task<IReadOnlyList<FavoriteGroup>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>新增分组；名称已存在时返回既有记录。</summary>
    /// <param name="name">分组名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<FavoriteGroup> AddAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>重命名分组。</summary>
    /// <param name="id">分组主键。</param>
    /// <param name="name">新名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RenameAsync(long id, string name, CancellationToken cancellationToken = default);

    /// <summary>删除分组；其下关联行由数据库级联删除，条目收藏状态不变。</summary>
    /// <param name="id">分组主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>批量设定条目与分组的归属关系。</summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="mediaIds">条目主键集合。</param>
    /// <param name="isMember">true 为加入分组（同时置为已收藏），false 为移出分组（不动收藏状态）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetMembershipAsync(
        long groupId,
        IReadOnlyList<long> mediaIds,
        bool isMember,
        CancellationToken cancellationToken = default);

    /// <summary>清除条目在全部分组中的归属，用于取消收藏与删除条目。</summary>
    /// <param name="mediaIds">条目主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ClearMembershipAsync(
        IReadOnlyList<long> mediaIds,
        CancellationToken cancellationToken = default);

    /// <summary>按条目主键批量取回其所属分组主键，供界面还原勾选态。</summary>
    /// <param name="mediaIds">条目主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目主键到分组主键集合的映射；无归属的条目对应空集合。</returns>
    Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetGroupIdsByMediaAsync(
        IReadOnlyList<long> mediaIds,
        CancellationToken cancellationToken = default);
}
