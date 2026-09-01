/**
 * 媒体元数据后台回填服务（M3）。
 * 职责：分批取出尚未探测的索引条目，读取其尺寸与时长后写回索引，使界面加载时不必逐个打开文件探尺寸。
 * 复用约定：探测能力经 IMediaMetadataProbe 注入，领域层不依赖任何平台解码 API；
 *          时间取自注入的 TimeProvider，进度经 IProgress 上报，与 MediaIndexingService 保持同一套约定。
 * 关键约束：探测成功与失败都必须置位 metadata_state，否则坏文件会被无限重试、永远占满批次额度，
 *          把真正可处理的条目挤在后面——失败置位是回填能收敛的前提；
 *          并发度必须远低于界面侧的尺寸预取：回填是常驻后台任务，与用户浏览的缩略图解码争抢 IO
 *          会直接拖慢正在浏览的页面，宁可慢也不能抢；
 *          写回只允许覆盖 width / height / duration_ms，收藏、评分、分类等用户数据一律不得触碰。
 */

using System.Collections.Concurrent;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>媒体元数据后台回填服务。</summary>
public sealed class MediaMetadataBackfillService
{
    private const int BatchSize = 200;

    /// <summary>探测并发度：后台常驻任务，须远低于界面侧预取的并发，避免与缩略图解码争抢 IO。</summary>
    private const int ProbeConcurrency = 2;

    private readonly IMediaItemRepository _mediaItems;
    private readonly IMediaMetadataProbe _probe;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化元数据回填服务。</summary>
    /// <param name="mediaItems">媒体条目仓储。</param>
    /// <param name="probe">元数据探测器。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public MediaMetadataBackfillService(
        IMediaItemRepository mediaItems,
        IMediaMetadataProbe probe,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(probe);

        _mediaItems = mediaItems;
        _probe = probe;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>回填一批未探测条目。</summary>
    /// <param name="progress">进度上报器；可为 null，上报值为本批已处理条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本批处理条数；为 0 表示已无待探测条目。</returns>
    public async Task<int> BackfillBatchAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await _mediaItems
            .GetMetadataPendingAsync(BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        var updates = new ConcurrentBag<MediaMetadataUpdate>();

        // 探测与写回分离：先并发读完全批再一次性提交，避免逐条往返把事务开销摊到每条记录上。
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ProbeConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                var result = await _probe
                    .ProbeAsync(item.Path, item.Kind == MediaKind.Video, token)
                    .ConfigureAwait(false);

                updates.Add(new MediaMetadataUpdate(item.Id, result, _timeProvider.GetUtcNow()));
            });

        await _mediaItems
            .UpdateMetadataBatchAsync([.. updates], cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(updates.Count);
        return updates.Count;
    }

    /// <summary>持续回填直至没有待探测条目或任务被取消。</summary>
    /// <param name="progress">进度上报器；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次累计处理条数。</returns>
    /// <remarks>
    /// 每批都重新查询待处理集合而非一次性取全量：批量之间文件可能已被删除或移动，
    /// 复用陈旧列表会对着不存在的文件反复探测。
    /// </remarks>
    public async Task<int> BackfillAllAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var processed = await BackfillBatchAsync(progress, cancellationToken).ConfigureAwait(false);

            if (processed == 0)
            {
                break;
            }

            total += processed;
        }

        return total;
    }
}
