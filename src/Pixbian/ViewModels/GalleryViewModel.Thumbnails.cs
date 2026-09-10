/**
 * 图库页视图模型——缩略图管线（partial）。
 * 职责：驱动缩略图解码调度器（视口窗口 + 距中心优先级 + 批次节奏）、内存缓存容量淘汰的
 *      回写（含视口内条目延后释放）、档位切换与按 DPI 刷新，以及分页条目的尺寸预取。
 * 复用约定：解码提交统一经 ThumbnailLoadScheduler；位图创建（DependencyObject）一律
 *          经 IUiDispatcher 切回 UI 线程；尺寸探测复用 IThumbnailService.GetDimensionsAsync。
 * 关键约束：视口内被容量淘汰的条目不得立即置空（LRU 时间戳停留在解码时刻会成为首选
 *          淘汰对象，表现为「图显示后又消失」）——登记延后，滚出视口再释放；
 *          宽高比批量写回前须复核加载代数，过期写回会白白驱动一轮全量重排。
 */

using System.Collections.Concurrent;
using Pixbian.Core.Models;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>图库页视图模型的缩略图管线。</summary>
public sealed partial class GalleryViewModel
{
    /// <summary>首屏优先提交的条目数：约 1 屏可视条目（等高视图行高 192 时约 32 条）加余量。
    /// 撤层（覆盖层消失）只等这批完成——机械盘首过全量解码时，该值直接决定切换目录的
    /// 感知等待；余下条目由调度器按视口渐进补齐。</summary>
    private const int FirstScreenSubmitCount = 40;

    /// <summary>缩略图提交批的批次大小：位图创建与视觉状态切换都在 UI 线程，批次越小
    /// 单次回调洪峰越短，批间让出后 UI 保持可交互（点击 / 滚动可随时插队）。</summary>
    private const int ThumbnailBatchSize = 10;

    /// <summary>提交批之间的让出间隔：给输入与渲染留执行窗。</summary>
    private static readonly TimeSpan ThumbnailBatchGap = TimeSpan.FromMilliseconds(40);

    /// <summary>整页缩略图解码的等待上限。单条编码已在服务层限时，此上限兜底「状态机
    /// 不被解码拖死」：超时后加载流程照常收口（LOADTOTAL/撤 loading），未完成的解码
    /// 在后台继续，位图就绪后经属性通知自然渐入，无需重试机制。</summary>
    private static readonly TimeSpan ThumbnailWaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>尺寸预取的并发度：只读文件头，并发远快于串行，但过高会与缩略图解码争抢 IO。</summary>
    private const int DimensionPrefetchConcurrency = 4;

    /// <summary>缩略图解码调度器：视口窗口驱动提交，解码量与集合规模解耦。</summary>
    private readonly ThumbnailLoadScheduler _scheduler;

    /// <summary>视口内被容量淘汰、等待滚出后再置空的条目。</summary>
    private readonly HashSet<MediaItemViewModel> _deferredEvictions = [];

    private int _thumbnailSize = ThumbnailSizes.Default;

    /// <summary>内存缓存容量淘汰回调（线程池触发）：回 UI 线程置空对应条目，交还调度器按视口恢复。</summary>
    /// <remarks>
    /// 视口内条目**不立即置空**：条目显示期间不访问内存缓存，其 LRU 时间戳停留在解码时刻，
    /// 容量触顶时反而成为首选淘汰对象——照单置空会表现为「缩略图显示后又消失」。
    /// 视口内条目登记延后，等滚出视口（UpdateViewport）再置空归还内存。
    /// </remarks>
    private void OnThumbnailEvicted(string path)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var items = Items;

            for (var i = 0; i < items.Count; i++)
            {
                var candidate = items[i];

                if (!string.Equals(candidate.Item.Path, path, StringComparison.Ordinal))
                {
                    continue;
                }

                if (_scheduler.IsInViewport(i))
                {
                    _deferredEvictions.Add(candidate);
                    return;
                }

                candidate.Thumbnail = null;
                return;
            }
        });
    }

    /// <summary>当前缩略图边长（像素）。</summary>
    public int ThumbnailSize => _thumbnailSize;

    /// <summary>设置缩略图尺寸，并按视口窗口重新加载条目缩略图。</summary>
    /// <param name="size">边长（像素）。</param>
    public async Task SetThumbnailSizeAsync(int size)
    {
        if (_thumbnailSize == size)
        {
            return;
        }

        _thumbnailSize = size;
        OnPropertyChanged(nameof(ThumbnailSize));

        // 档位切换改变解码桶，整批条目需重解；只解视口窗口，其余滚动到时恢复（虚拟化常态）。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in Items)
            {
                item.Thumbnail = null;
            }
        });

        _scheduler.RefreshViewport();
    }

    /// <summary>丢弃已加载的缩略图并按视口窗口重新加载，用于显示缩放比变化后按新的物理像素重新解码。</summary>
    public async Task RefreshThumbnailsAsync()
    {
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in Items)
            {
                item.Thumbnail = null;
            }
        });

        _scheduler.RefreshViewport();
    }

    /// <summary>视口区间更新（页面滚动停止时转发调度器）：收编窗口内待解条目并按优先级渐进提交。</summary>
    /// <param name="firstVisible">可见区间首个索引（含）。</param>
    /// <param name="lastVisible">可见区间末个索引（含）。</param>
    public void UpdateViewport(int firstVisible, int lastVisible)
    {
        _scheduler.UpdateViewport(firstVisible, lastVisible);

        if (_deferredEvictions.Count > 0)
        {
            ReleaseDeferredEvictions();
        }
    }

    /// <summary>置空已滚出视口的延后淘汰条目：视口内的保留位图，避免显示中的图被清空。</summary>
    private void ReleaseDeferredEvictions()
    {
        var items = Items;
        List<MediaItemViewModel>? released = null;

        foreach (var item in _deferredEvictions)
        {
            var index = items.IndexOf(item);

            if (index >= 0 && _scheduler.IsInViewport(index))
            {
                continue;
            }

            (released ??= []).Add(item);
        }

        if (released is null)
        {
            return;
        }

        foreach (var item in released)
        {
            _deferredEvictions.Remove(item);
            item.Thumbnail = null;
        }
    }

    /// <summary>并发预取条目尺寸，使布局在缩略图解码完成前就按真实宽高比排列。</summary>
    /// <param name="items">待预取的条目。</param>
    /// <param name="cancellationToken">代数级取消令牌；切换视图后旧预取立即停止。</param>
    /// <param name="sequence">发起时的加载代数；写回前复核，过期请求跳过整块写回
    /// （宽高比通知会驱动全量重排，过期写回纯属 UI 线程浪费）。</param>
    private async Task PrefetchDimensionsAsync(
        IReadOnlyList<MediaItemViewModel> items,
        int sequence,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<(MediaItemViewModel Item, int Width, int Height)>();

        // 探测与属性读取都不触碰 DependencyObject，可在线程池并行；只写回 UI 线程。
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DimensionPrefetchConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                var size = await _thumbnails.GetDimensionsAsync(item.Item.Path, token);

                if (size is not null)
                {
                    results.Add((item, size.Value.Width, size.Value.Height));
                }
            });

        if (results.IsEmpty)
        {
            return;
        }

        // 一次性写回：AspectRatio 变更会触发布局面板重测，逐条 await 会让 UI 线程切换成为瓶颈。
        // 写回前复核代数：探测耗时 1~3 秒，期间切走时过期写回只会白白驱动一轮全量重排。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            if (sequence != _loadSequence)
            {
                return;
            }

            foreach (var (item, width, height) in results)
            {
                item.SetDimensions(width, height);
            }

            // 虚拟化布局无条目 INPC 订阅机制，行几何表由订阅方据此重建。
            AspectRatiosApplied?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>为重置路径的首屏分批加载缩略图：首屏批同步等待保撤层时序，余量交调度器。</summary>
    /// <param name="pending">待加载缩略图的条目（本页全部新增）。</param>
    /// <param name="sequence">发起时的加载代数；切换视图后立即中止首屏批。</param>
    /// <remarks>
    /// 位图创建（SetSourceAsync 与视觉状态切换）都在 UI 线程执行，一次性提交整页 200 条
    /// 会让 UI 线程被解码回调与重排钉死数秒——表现为菜单点击排队（卡顿）。
    /// 首屏批（约 60 条）小批推进并等待完成，返回时首屏已就绪，调用方随即撤层；
    /// 首屏外的条目只入调度器待解队列，滚动到视口时才提交（解码量 = O(视口)）。
    /// </remarks>
    private async Task LoadThumbnailsForVisibleItemsAsync(List<MediaItemViewModel> pending, int sequence)
    {
        if (pending.Count == 0)
        {
            return;
        }

        // 首屏优先：虚拟化下可见约 40 条，先保证首屏出图。
        await SubmitThumbnailBatchesAsync(pending.Take(FirstScreenSubmitCount).ToList(), sequence);

        if (pending.Count <= FirstScreenSubmitCount || sequence != _loadSequence)
        {
            return;
        }

        // 余量入调度器待解队列：等待视口更新驱动，不立即提交。
        _scheduler.Enqueue(pending.Skip(FirstScreenSubmitCount).ToList());
    }

    /// <summary>把一批条目切成小批提交：每批仅 10 条，批间让出 UI 线程，并等待本组全部完成。</summary>
    private async Task SubmitThumbnailBatchesAsync(List<MediaItemViewModel> items, int sequence)
    {
        var tasks = new List<Task>(items.Count);

        for (var offset = 0; offset < items.Count; offset += ThumbnailBatchSize)
        {
            // 切换视图后立即中止剩余批：避免旧请求继续占用解码信号量与 UI 线程。
            if (sequence != _loadSequence)
            {
                return;
            }

            var batch = items.Skip(offset).Take(ThumbnailBatchSize).ToList();

            // EnsureThumbnailAsync 内部会创建 BitmapImage（DependencyObject，具线程亲和性），
            // 必须在 UI 线程发起；此处只收集任务，不能在 lambda 内 await，否则会自我死锁。
            await _dispatcherQueue.EnqueueAsync(() =>
            {
                foreach (var item in batch)
                {
                    tasks.Add(item.EnsureThumbnailAsync(_thumbnailSize));
                }
            });

            // 批间让出 UI 线程：间隔内输入事件与渲染可插队，
            // 把「UI 被连续钉死数秒」化为「平滑渐进」。
            await Task.Delay(ThumbnailBatchGap).ConfigureAwait(false);
        }

        // 等待本组全部完成：防上一页解码与下一页请求叠加，队列越滚越长。
        // 超时仅让收口（防单条 IO 挂死拖死 IsLoading/CanLoadMore），
        // 解码任务仍在后台推进，就绪后由属性通知自然上屏。
        try
        {
            await Task.WhenAll(tasks).WaitAsync(ThumbnailWaitTimeout);
        }
        catch (TimeoutException)
        {
            // 超时仅让收口（防单条 IO 挂死拖死 IsLoading/CanLoadMore），
            // 解码任务仍在后台推进，就绪后由属性通知自然上屏。
        }
        catch (Exception)
        {
            // 积压批以弃任务方式运行，此处必须吞掉异常防未观察异常炸进程。
        }
    }
}
