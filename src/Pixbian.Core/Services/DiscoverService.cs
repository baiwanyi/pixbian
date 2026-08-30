/**
 * 发现模式的随机抽样服务（M6）。
 * 职责：从媒体库中随机抽取条目，并避免短期内重复展示同一文件。
 * 复用约定：随机性由应用层生成的偏移量提供，仓储层按主键定位——
 *          禁止在 SQL 层使用 ORDER BY RANDOM()，该写法会对全表排序，数据量上万后开销无法接受。
 * 关键约束：随机数必须使用密码学强度的 RandomNumberGenerator，
 *          用 Random.Shared 做取模会产生偏差且可预测，在"随机浏览"场景下会出现明显的重复模式；
 *          最近展示队列用于去重，队列满后 FIFO 淘汰；
 *          若候选数少于去重队列容量，重试次数耗尽后应放宽去重，否则会陷入死循环。
 */

using System.Security.Cryptography;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>发现模式的筛选范围。</summary>
public enum DiscoverScope
{
    /// <summary>仅图片。</summary>
    Images = 0,

    /// <summary>仅视频。</summary>
    Videos = 1,

    /// <summary>全部媒体。</summary>
    All = 2
}

/// <summary>随机抽样服务。</summary>
public sealed class DiscoverService
{
    private const int DefaultRecentCapacity = 200;
    private const int MaxAttempts = 8;

    private readonly IMediaItemRepository _mediaItems;
    private readonly Queue<long> _recentIds;
    private readonly HashSet<long> _recentLookup;
    private readonly int _recentCapacity;

    private int _totalCount = -1;
    private MediaKind? _cachedKind;

    /// <summary>初始化抽样服务。</summary>
    /// <param name="mediaItems">媒体条目仓储。</param>
    /// <param name="recentCapacity">去重队列容量；小于等于 0 时使用默认值。</param>
    public DiscoverService(IMediaItemRepository mediaItems, int recentCapacity = DefaultRecentCapacity)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);

        _mediaItems = mediaItems;
        _recentCapacity = recentCapacity > 0 ? recentCapacity : DefaultRecentCapacity;
        _recentIds = new Queue<long>(_recentCapacity);
        _recentLookup = new HashSet<long>(_recentCapacity);
    }

    /// <summary>随机抽取一条条目。</summary>
    /// <param name="scope">筛选范围。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>抽中的条目；库为空时返回 null。</returns>
    public async Task<MediaItem?> PickRandomAsync(
        DiscoverScope scope = DiscoverScope.All,
        CancellationToken cancellationToken = default)
    {
        var kind = ToMediaKind(scope);
        var total = await GetTotalAsync(kind, cancellationToken).ConfigureAwait(false);

        if (total <= 0)
        {
            return null;
        }

        // 候选数不多于去重队列容量时，重试再多也必然重复，直接放宽去重避免死循环。
        var attempts = total > _recentCapacity ? MaxAttempts : 1;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var candidate = await PickAtRandomOffsetAsync(kind, total, cancellationToken)
                .ConfigureAwait(false);

            if (candidate is null)
            {
                continue;
            }

            if (!_recentLookup.Contains(candidate.Id))
            {
                Remember(candidate.Id);
                return candidate;
            }
        }

        // 重试耗尽或库太小：接受一个可能重复的结果，保证界面不会卡住。
        var fallback = await PickAtRandomOffsetAsync(kind, total, cancellationToken)
            .ConfigureAwait(false);

        if (fallback is not null)
        {
            Remember(fallback.Id);
        }

        return fallback;
    }

    /// <summary>清空去重队列与总数缓存。</summary>
    /// <remarks>媒体库发生增删后必须调用，否则会沿用失效的总数导致抽样越界。</remarks>
    public void Reset()
    {
        _recentIds.Clear();
        _recentLookup.Clear();
        _totalCount = -1;
        _cachedKind = null;
    }

    /// <summary>按随机偏移量定位单条条目。</summary>
    private async Task<MediaItem?> PickAtRandomOffsetAsync(
        MediaKind? kind,
        int total,
        CancellationToken cancellationToken)
    {
        var offset = RandomNumberGenerator.GetInt32(total);
        return await _mediaItems.GetAtOffsetAsync(kind, offset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>获取并缓存符合范围的条目总数。</summary>
    private async Task<int> GetTotalAsync(MediaKind? kind, CancellationToken cancellationToken)
    {
        if (_totalCount >= 0 && _cachedKind == kind)
        {
            return _totalCount;
        }

        _totalCount = await _mediaItems.CountAsync(kind, cancellationToken).ConfigureAwait(false);
        _cachedKind = kind;
        return _totalCount;
    }

    /// <summary>把条目记入去重队列；队列满则淘汰最早的一条。</summary>
    private void Remember(long id)
    {
        if (_recentLookup.Add(id))
        {
            _recentIds.Enqueue(id);
        }

        while (_recentIds.Count > _recentCapacity)
        {
            var evicted = _recentIds.Dequeue();
            _recentLookup.Remove(evicted);
        }
    }

    private static MediaKind? ToMediaKind(DiscoverScope scope) => scope switch
    {
        DiscoverScope.Images => MediaKind.Image,
        DiscoverScope.Videos => MediaKind.Video,
        _ => null
    };
}
