/**
 * 发现模式视图模型（M6）。
 * 职责：驱动随机浏览——抽取条目、定时切换、收藏与跳过，并管理缩略图加载。
 * 复用约定：抽样走 DiscoverService，切换由 DispatcherQueueTimer 驱动；
 *          所有集合与状态更新一律切回 UI 线程（仓储内部使用 ConfigureAwait(false)）。
 * 关键约束：定时器必须在停止播放或离开页面时停止，否则会残留后台计时器持续触发；
 *          切换间隔须做下限钳制，过小的间隔会让缩略图解码来不及完成，导致界面闪烁；
 *          媒体库发生增删后必须 Reset 抽样服务，否则会沿用失效的总数导致抽样越界。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>发现模式视图模型。</summary>
public sealed partial class DiscoverViewModel : ObservableObject, IDisposable
{
    private const int MinIntervalSeconds = 1;
    private const int MaxIntervalSeconds = 3600;

    private static readonly int[] IntervalPresets = [3, 5, 10, 30];

    private readonly DiscoverService _discover;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IThumbnailService _thumbnails;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _timer;

    private int _intervalIndex = 1;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private MediaItemViewModel? _current;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _statusText = "点击开始，随机浏览媒体库";

    [ObservableProperty]
    private DiscoverScope _scope = DiscoverScope.All;

    public DiscoverViewModel(
        DiscoverService discover,
        IMediaItemRepository mediaItems,
        IThumbnailService thumbnails,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(thumbnails);

        _discover = discover;
        _mediaItems = mediaItems;
        _thumbnails = thumbnails;

        var queue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _dispatcherQueue = queue;
        _timer = queue.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TimeSpan.FromSeconds(IntervalPresets[_intervalIndex]);
        _timer.Tick += OnTimerTick;
    }

    /// <summary>可选的切换间隔（秒）。</summary>
    public static IReadOnlyList<int> IntervalOptions => IntervalPresets;

    /// <summary>当前切换间隔的索引。</summary>
    public int IntervalIndex
    {
        get => _intervalIndex;
        set
        {
            if (_intervalIndex == value || value < 0 || value >= IntervalPresets.Length)
            {
                return;
            }

            _intervalIndex = value;
            _timer.Interval = TimeSpan.FromSeconds(
                Math.Clamp(IntervalPresets[value], MinIntervalSeconds, MaxIntervalSeconds));

            OnPropertyChanged();
        }
    }

    /// <summary>是否为空库。</summary>
    public bool IsEmpty => Current is null;

    /// <summary>切换筛选范围并立即换一张。</summary>
    /// <param name="scope">目标范围。</param>
    public async Task SetScopeAsync(DiscoverScope scope)
    {
        if (Scope == scope)
        {
            return;
        }

        Scope = scope;
        _discover.Reset();

        await NextAsync();
    }

    /// <summary>随机切换到下一条。</summary>
    [RelayCommand]
    public async Task NextAsync()
    {
        // 取消上一次未完成的缩略图加载，避免快速切换时旧结果覆盖新结果。
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();

        var item = await _discover.PickRandomAsync(Scope, _loadCts.Token);

        // PickRandomAsync 内部全程 ConfigureAwait(false)，await 之后当前线程已是线程池线程。
        // StatusText 触发的 PropertyChanged、Current 指向的 BitmapImage 都必须在 UI 线程更新，
        // 在非 UI 线程触碰会直接导致 XAML 层原生崩溃（0xc000027b）。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            if (item is null)
            {
                StatusText = "媒体库为空，请先到「设置」中添加文件夹并索引";
                return;
            }

            var viewModel = new MediaItemViewModel(item, _thumbnails.LoadThumbnailAsyncCore);

            var previous = Current;
            previous?.CancelPendingLoad();

            Current = viewModel;
            OnPropertyChanged(nameof(IsEmpty));
            FavoriteCommand.NotifyCanExecuteChanged();
            SkipCommand.NotifyCanExecuteChanged();

            StatusText = $"{viewModel.FileName} · {viewModel.TakenDateText}";
        });

        // 缩略图解码较慢，不等待其完成；但必须在 UI 线程发起，
        // 使其内部的第一个 await 之后能回到 UI 上下文（BitmapImage 只能在 UI 线程创建）。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            if (Current is not null)
            {
                _ = Current.EnsureThumbnailAsync(320, _loadCts.Token);
            }
        });
    }

    /// <summary>收藏当前条目。</summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    public async Task FavoriteAsync()
    {
        if (Current is null)
        {
            return;
        }

        var fileName = Current.FileName;

        // SetFavoriteAsync 内部使用 ConfigureAwait(false)，状态更新须切回 UI 线程。
        await _mediaItems.SetFavoriteAsync([Current.Id], true);
        await _dispatcherQueue.EnqueueAsync(() => StatusText = $"已收藏：{fileName}");

        await NextAsync();
    }

    /// <summary>跳过当前条目，等同切换到下一条。</summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    public Task SkipAsync() => NextAsync();

    /// <summary>开始定时切换。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public void Start()
    {
        if (IsPlaying)
        {
            return;
        }

        IsPlaying = true;
        _timer.Start();
        StatusText = "正在自动播放…";
    }

    /// <summary>停止定时切换。</summary>
    [RelayCommand]
    public void Stop()
    {
        IsPlaying = false;
        _timer.Stop();
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>切换播放状态。</summary>
    [RelayCommand]
    public void TogglePlay()
    {
        if (IsPlaying)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    /// <summary>媒体库发生变化后重置抽样缓存。</summary>
    public void ResetLibrary() => _discover.Reset();

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    /// <summary>是否存在当前条目，决定收藏与跳过按钮的可用性。</summary>
    public bool HasCurrent => Current is not null;

    private bool CanStart() => !IsPlaying;

    private async void OnTimerTick(DispatcherQueueTimer sender, object args) => await NextAsync();
}
