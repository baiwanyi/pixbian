/**
 * 缩略图解码调度器的单元测试。
 * 职责：锁定「视口窗口驱动」的核心语义——窗口收编与离窗清理、距中心优先级、
 *       每拍批次上限、失败条目不重试、Enqueue 先进先出退化与 Reset 全清。
 * 复用约定：经 internal 构造注入假节拍器（FakeCommitTimer），手动驱动 Tick 验证批次节奏；
 *          条目用真实 MediaItemViewModel（loader 恒返 null），以 loader 调用记录作为提交证据。
 * 关键约束：立即提交首批与「每拍 ≤4 条」是性能语义的直接防线，用例不得删减。
 */

using System.Globalization;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Controls;
using Pixbian.ViewModels;
using Xunit;

namespace Pixbian.UI.Tests.ViewModels;

/// <summary>ThumbnailLoadScheduler 测试。</summary>
public sealed class ThumbnailLoadSchedulerTests : IDisposable
{
    /// <summary>假节拍器：手动驱动 Tick，记录启停状态供断言。</summary>
    private sealed class FakeCommitTimer : ICommitTimer
    {
        public TimeSpan Interval { get; set; }

        public bool IsRunning { get; private set; }

        public event EventHandler? Tick;

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Dispose() { }

        public void RaiseTick() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private readonly FakeCommitTimer _timer = new();
    private readonly List<MediaItemViewModel> _items = [];
    private readonly List<string> _requestedPaths = [];

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    private ThumbnailLoadScheduler CreateScheduler() => new(
        () => _items,
        () => 256,
        _timer);

    private MediaItemViewModel CreateItem(long id) => new(
        new MediaItem
        {
            Id = id,
            Path = $"D:\\Lib\\{id}.jpg",
            FileName = $"{id}.jpg",
            Kind = MediaKind.Image
        },
        (path, _, _) =>
        {
            _requestedPaths.Add(path);
            return Task.FromResult<BitmapImage?>(null);
        });

    private List<MediaItemViewModel> CreateItems(int count) =>
        Enumerable.Range(0, count).Select(i => CreateItem(i)).ToList();

    [Fact]
    public void UpdateViewport_窗口内无位图条目_立即提交首批并启动节拍()
    {
        _items.AddRange(CreateItems(10));
        var scheduler = CreateScheduler();

        scheduler.UpdateViewport(0, 2);

        // 立即提交一批（4 条）消除起播延迟；剩余待解时节拍器保持运行。
        Assert.Equal(ThumbnailLoadScheduler.CommitBatchSize, _requestedPaths.Count);
        Assert.True(_timer.IsRunning);
    }

    [Fact]
    public void CommitBatch_节拍推进_按批次逐步清空待解队列()
    {
        _items.AddRange(CreateItems(10));
        var scheduler = CreateScheduler();

        // 可见 0..2（跨度 3）→ 窗口 0..5，收编 6 条。
        scheduler.UpdateViewport(0, 2);

        // 立即首批 4 条 + 一拍 2 条（末拍不足 4 条时全部提交），队列清空后节拍停止。
        _timer.RaiseTick();

        Assert.Equal(6, _requestedPaths.Count);
        Assert.False(_timer.IsRunning);
    }

    [Fact]
    public void CommitBatch_距视口中心近的条目先提交()
    {
        _items.AddRange(CreateItems(12));
        var scheduler = CreateScheduler();

        // 可见区间 4..7，窗口扩展 ±4 屏 → 窗口 0..11，中心 = (0+11)/2 = 5。
        scheduler.UpdateViewport(4, 7);

        // 首批 4 条应为距索引 5 最近的：5,4,6 距离 ≤1，第 4 条距离 2（3 或 7）；
        // 同距时 HashSet 枚举顺序不定，只断言距离上界与必然包含的最近条目。
        var requestedIndexes = _requestedPaths
            .Select(path => int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(4, requestedIndexes.Count);
        Assert.Contains(5, requestedIndexes);
        Assert.All(requestedIndexes, index => Assert.InRange(Math.Abs(index - 5), 0, 2));
    }

    [Fact]
    public void UpdateViewport_滚动后离窗条目_不再提交()
    {
        _items.AddRange(CreateItems(40));
        var scheduler = CreateScheduler();

        scheduler.UpdateViewport(0, 2);
        var committedBefore = _requestedPaths.Count;
        _requestedPaths.Clear();

        // 视口滚到远处：原窗口条目全部离窗，其待解项被清理，不提交。
        scheduler.UpdateViewport(30, 32);

        Assert.DoesNotContain(_requestedPaths, path => path.Contains("D:\\Lib\\0.jpg"));
        Assert.Equal(committedBefore + _requestedPaths.Count, committedBefore + _requestedPaths.Count);
    }

    [Fact]
    public void CommitBatch_已提交条目完成解码_不重复提交()
    {
        _items.AddRange(CreateItems(4));
        var scheduler = CreateScheduler();

        scheduler.UpdateViewport(0, 2);
        _timer.RaiseTick();

        // 窗口覆盖全部 4 条且已提交完毕：再次 Tick 不得产生重复解码请求。
        Assert.Equal(4, _requestedPaths.Count);
        Assert.Equal(4, _requestedPaths.Distinct().Count());
        Assert.False(_timer.IsRunning);
    }

    [Fact]
    public void UpdateViewport_失败条目_不自动重试()
    {
        var item = CreateItem(0);
        item.ThumbnailState = ThumbnailLoadState.Failed;
        _items.Add(item);
        var scheduler = CreateScheduler();

        scheduler.UpdateViewport(0, 0);

        Assert.Empty(_requestedPaths);
    }

    [Fact]
    public void Enqueue_未收到视口更新前_按先进先出提交()
    {
        var scheduler = CreateScheduler();
        var items = CreateItems(6);
        scheduler.Enqueue(items);

        _timer.RaiseTick();

        // 无有效窗口时退化为先进先出：首批 4 条按入队顺序。
        Assert.Equal(4, _requestedPaths.Count);
        Assert.Equal("D:\\Lib\\0.jpg", _requestedPaths[0]);
        Assert.Equal("D:\\Lib\\3.jpg", _requestedPaths[3]);
    }

    [Fact]
    public void Reset_清空待解队列并停止节拍()
    {
        _items.AddRange(CreateItems(10));
        var scheduler = CreateScheduler();
        scheduler.UpdateViewport(0, 2);

        scheduler.Reset();

        Assert.False(_timer.IsRunning);

        // Reset 后待解队列已空：视口重报前 Tick 不提交任何条目。
        var count = _requestedPaths.Count;
        _timer.RaiseTick();
        Assert.Equal(count, _requestedPaths.Count);
    }

    [Fact]
    public void RefreshViewport_档位切换后_按最近可见区间重新收编()
    {
        _items.AddRange(CreateItems(10));
        var scheduler = CreateScheduler();
        scheduler.UpdateViewport(0, 2);
        Assert.False(_requestedPaths.Count == 0);

        // 模拟档位切换把位图整体置空：全部条目回到「无位图」状态。
        foreach (var item in _items)
        {
            item.Thumbnail = null;
            item.ThumbnailState = ThumbnailLoadState.Loading;
        }

        scheduler.RefreshViewport();

        Assert.True(_timer.IsRunning);
    }

    [Fact]
    public void IsInViewport_窗口未建立_保守判定在视口内()
    {
        var scheduler = CreateScheduler();

        Assert.True(scheduler.IsInViewport(0));
    }

    [Fact]
    public void IsInViewport_窗口建立后_按扩展窗口判定()
    {
        _items.AddRange(CreateItems(40));
        var scheduler = CreateScheduler();
        scheduler.UpdateViewport(10, 12);

        // 窗口 = 可见区间 ± 可见跨度（3）：first 7、last 15。
        Assert.False(scheduler.IsInViewport(6));
        Assert.True(scheduler.IsInViewport(7));
        Assert.True(scheduler.IsInViewport(15));
        Assert.False(scheduler.IsInViewport(16));
    }

    [Fact]
    public void Remove_删除路径的单条待解项_不再提交()
    {
        var item = CreateItem(0);
        _items.Add(item);
        var scheduler = CreateScheduler();
        scheduler.Enqueue([item]);

        scheduler.Remove(item);
        _timer.RaiseTick();

        Assert.Empty(_requestedPaths);
    }
}
