/**
 * 缩略图解码调度器（图库列表性能优化 P1b 核心）。
 * 职责：把图库条目的缩略图解码收敛为「视口窗口驱动」——只解码视口 ±1 屏内的条目，
 *      按距视口中心距离优先提交，使解码请求数与集合规模解耦（解码量 = O(视口)）。
 *      取代原先「整页提交 + 承间让出」与「全量扫描恢复」两条 O(n) 路径。
 * 复用约定：解码一律经条目的 EnsureThumbnailAsync 发起（DependencyObject 须在 UI 线程），
 *          并发由 ThumbnailService 的解码信号量限流；本调度器只裁决「谁先解、何时解」，
 *          不触碰解码与缓存本身；离窗在途取消仍由页面的窗口差集逻辑承担。
 * 关键约束：提交经 DispatcherQueueTimer 在 UI 线程执行，每 tick ≤4 条防止位图创建洪峰；
 *          禁止用 CompositionTarget.Rendering 错峰（本项目实证其与渲染 tick 抢占执行窗，
 *          会令布局 pass 永久停摆）；窗口内无位图条目每次视口更新时重新收编，
 *          被内存缓存淘汰（容量/过期置空）的条目因此自然恢复，无需独立登记集合。
 */

using Microsoft.UI.Dispatching;
using Pixbian.Controls;

namespace Pixbian.ViewModels;

/// <summary>提交节拍器抽象：隔离 DispatcherQueueTimer 的 UI 亲和，测试注入假节拍器手动驱动 Tick。</summary>
internal interface ICommitTimer : IDisposable
{
    /// <summary>节拍间隔；Start 前设置。</summary>
    TimeSpan Interval { get; set; }

    /// <summary>启动节拍。</summary>
    void Start();

    /// <summary>停止节拍（幂等）。</summary>
    void Stop();

    /// <summary>每个节拍触发一次。</summary>
    event EventHandler? Tick;
}

/// <summary>生产节拍器：包装 DispatcherQueueTimer（须在 UI 线程创建）。</summary>
internal sealed class DispatcherCommitTimer : ICommitTimer
{
    private readonly DispatcherQueueTimer _timer;

    public DispatcherCommitTimer(DispatcherQueueTimer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        _timer = timer;
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public event EventHandler? Tick;

    public void Dispose() => _timer.Stop();
}

/// <summary>缩略图解码调度器。</summary>
public sealed class ThumbnailLoadScheduler : IDisposable
{
    /// <summary>提交节拍间隔：约一帧时长，tick 间让出 UI 线程给输入与渲染。</summary>
    internal static readonly TimeSpan CommitInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>每个提交节拍最多发起的解码条数：位图创建与视觉状态切换都在 UI 线程。</summary>
    internal const int CommitBatchSize = 4;

    private readonly Func<IReadOnlyList<MediaItemViewModel>> _itemsProvider;
    private readonly Func<int> _thumbnailSizeProvider;
    private readonly ICommitTimer _commitTimer;

    /// <summary>当前视口窗口（已含 ±1 屏扩展）；未就绪为 (-1, -1)。</summary>
    private (int First, int Last) _window = (-1, -1);

    /// <summary>最近一次上报的可见区间（未扩展），供 RefreshViewport 重派生窗口。</summary>
    private (int First, int Last) _visible = (-1, -1);

    /// <summary>窗口内条目 → 数据索引：优先级排序 O(1) 查询，随窗口重建。</summary>
    private Dictionary<MediaItemViewModel, int>? _windowIndex;

    /// <summary>待解码条目（窗口内、尚无位图）；提交或离窗时移出。</summary>
    private readonly HashSet<MediaItemViewModel> _pending = [];

    /// <summary>初始化调度器；三个委托由图库视图模型提供，避免反向引用。</summary>
    public ThumbnailLoadScheduler(
        Func<IReadOnlyList<MediaItemViewModel>> itemsProvider,
        Func<int> thumbnailSizeProvider,
        DispatcherQueue dispatcher)
        : this(itemsProvider, thumbnailSizeProvider, new DispatcherCommitTimer(dispatcher.CreateTimer()))
    {
    }

    /// <summary>internal 构造：节拍器可注入，供测试手动驱动提交节拍。</summary>
    internal ThumbnailLoadScheduler(
        Func<IReadOnlyList<MediaItemViewModel>> itemsProvider,
        Func<int> thumbnailSizeProvider,
        ICommitTimer commitTimer)
    {
        ArgumentNullException.ThrowIfNull(itemsProvider);
        ArgumentNullException.ThrowIfNull(thumbnailSizeProvider);
        ArgumentNullException.ThrowIfNull(commitTimer);

        _itemsProvider = itemsProvider;
        _thumbnailSizeProvider = thumbnailSizeProvider;

        _commitTimer = commitTimer;
        _commitTimer.Interval = CommitInterval;
        _commitTimer.Tick += (_, _) => CommitBatch();
    }

    /// <summary>更新视口区间：重算窗口、收编窗口内无位图条目、清理离窗待解项。</summary>
    /// <param name="firstVisible">可见区间首个索引（含）。</param>
    /// <param name="lastVisible">可见区间末个索引（含）。</param>
    public void UpdateViewport(int firstVisible, int lastVisible)
    {
        _visible = (firstVisible, lastVisible);

        UpdateWindow(firstVisible, lastVisible);
    }

    /// <summary>用最近一次可见区间重新收编：档位切换 / 缩放比变化后条目被整体置空时调用。</summary>
    public void RefreshViewport()
    {
        if (_visible.First >= 0)
        {
            UpdateWindow(_visible.First, _visible.Last);
        }
    }

    /// <summary>索引是否落在当前解码窗口内（可见区间 ±1 屏）。</summary>
    /// <remarks>
    /// 供容量淘汰回调判定「能否安全置空」：视口内条目显示中不再访问内存缓存，其 LRU
    /// 时间戳停留在解码时刻，容量触顶时反而最先被淘汰——若照单置空就会出现
    /// 「缩略图显示后又消失」。窗口内条目延后到滚出视口再置空（登记在调用方）。
    /// </remarks>
    public bool IsInViewport(int index)
    {
        // 窗口尚未建立（(-1,-1)，例如从未收到过视口上报）时无法判定，保守视为「在视口内」：
        // 判为在视口内最多让内存回收延后，判为不在视口内会把正在显示的条目置空成骨架屏。
        if (_window.First < 0)
        {
            return true;
        }

        return index >= _window.First && index <= _window.Last;
    }

    /// <summary>由可见区间派生窗口并执行收编与离窗清理。</summary>
    private void UpdateWindow(int firstVisible, int lastVisible)
    {
        var items = _itemsProvider();
        var count = items.Count;

        if (count == 0 || firstVisible < 0 || lastVisible < firstVisible)
        {
            Reset();
            return;
        }

        var span = lastVisible - firstVisible + 1;
        var winFirst = Math.Max(0, firstVisible - span);
        var winLast = Math.Min(count - 1, lastVisible + span);
        _window = (winFirst, winLast);

        // 窗口条目索引：收编、离窗清理与优先级排序共用，构建 O(窗口)。
        var windowIndex = new Dictionary<MediaItemViewModel, int>(winLast - winFirst + 1);

        for (var i = winFirst; i <= winLast; i++)
        {
            var item = items[i];
            windowIndex[item] = i;

            // 无位图条目（新增 / 被内存缓存淘汰 / 被取消）重新收编；失败条目不自动重试。
            if (item.Thumbnail is null && item.ThumbnailState != ThumbnailLoadState.Failed)
            {
                _pending.Add(item);
            }
        }

        // 离窗待解项移出队列（不再提交）；其取消由页面窗口差集负责。
        _pending.RemoveWhere(item => !windowIndex.ContainsKey(item));
        _windowIndex = windowIndex;

        if (_pending.Count == 0)
        {
            return;
        }

        // 立即提交一个批次再交节拍器接续：消除「收编后等 33ms 首拍」的起播延迟。
        CommitBatch();

        if (_pending.Count > 0)
        {
            _commitTimer.Start();
        }
    }

    /// <summary>把翻页新增条目加入待解队列（等待视口驱动，不立即提交）。</summary>
    public void Enqueue(IReadOnlyList<MediaItemViewModel> items)
    {
        foreach (var item in items)
        {
            _pending.Add(item);
        }

        if (_pending.Count > 0)
        {
            _commitTimer.Start();
        }
    }

    /// <summary>清空待解队列并停止节拍；切换视图 / 筛选 / 删除条目时调用。</summary>
    public void Reset()
    {
        _commitTimer.Stop();
        _pending.Clear();
        _windowIndex = null;
        _window = (-1, -1);
        _visible = (-1, -1);
    }

    /// <summary>单条待解项在删除路径移除，避免提交已不存在的条目。</summary>
    public void Remove(MediaItemViewModel item) => _pending.Remove(item);

    /// <summary>提交一个批次的待解条目：按距视口中心距离取最近的若干条。</summary>
    private void CommitBatch()
    {
        if (_pending.Count == 0)
        {
            _commitTimer.Stop();
            return;
        }

        var size = _thumbnailSizeProvider();

        // 无有效窗口时退化为先进先出（Enqueue 后未收到视口更新前的短暂状态）。
        List<MediaItemViewModel> batch;

        if (_windowIndex is { } windowIndex)
        {
            var center = (_window.First + _window.Last) / 2;

            // Enqueue 的条目可能尚未纳入窗口（未收到视口更新）：按无穷远排到最后，待窗口刷新后自然前移。
            batch = _pending
                .Select(item => windowIndex.TryGetValue(item, out var index)
                    ? (Item: item, Distance: Math.Abs(index - center))
                    : (Item: item, Distance: int.MaxValue))
                .OrderBy(x => x.Distance)
                .Take(CommitBatchSize)
                .Select(x => x.Item)
                .ToList();
        }
        else
        {
            batch = _pending.Take(CommitBatchSize).ToList();
        }

        foreach (var item in batch)
        {
            _pending.Remove(item);

            // fire-and-forget：解码在服务层信号量限流，完成后经属性通知上屏。
            _ = item.EnsureThumbnailAsync(size);
        }

        if (_pending.Count == 0)
        {
            _commitTimer.Stop();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _commitTimer.Stop();
}
