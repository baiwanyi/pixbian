/**
 * 幻灯片放映视图模型。
 * 职责：管理放映列表与游标推进（列表 / 随机），按设置应用间隔、切换方式与视频策略，
 *      装载当前条目（图片走位图管线并做 EXIF 方向校正，视频经播放项工厂走解码管线），
 *      并经转场事件通知页面播动画。
 * 复用约定：游标与洗牌由 SlideShowSequencer 承担，本类不重复实现序列语义；
 *          视频播放项复用 IVideoPlaybackItemFactory（FFmpeg 优先 + 系统解码回退），
 *          解码后端按编码选择所需的元数据经 IVideoMetadataReader 读取；
 *          放映配置由外壳在设置变更时经 ApplySettings 推送，本类不反向依赖设置服务；
 *          页面播完转场动画必须回调 CompleteTransition，否则旧帧长期驻留内存。
 * 关键约束：装载序号必须防快速翻页错配——旧条目异步装载完成后不得覆盖新条目的显示；
 *          视频条目不按间隔计时，由播放结束事件驱动推进，装载成功必须停表、
 *          失败须保持计时以按间隔跳过坏条目，否则放映会卡死或快速循环；
 *          播放项必须随切换、翻页、停止与销毁 Dispose，否则 FFmpeg 解码上下文与文件句柄滞留；
 *          停止放映与页面卸载时必须停定时器；事件在 await 之后的线程上发出，
 *          页面必须自行切回 UI 线程播动画。
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Imaging.Services;
using Pixbian.Media.Models;
using Pixbian.Media.Services;
using Pixbian.Services;
using Windows.Media.Playback;
using Windows.Storage;

namespace Pixbian.ViewModels;

/// <summary>幻灯片放映视图模型。</summary>
public sealed partial class SlideShowViewModel : ObservableObject, IDisposable
{
    private readonly IImageMetadataReader _metadataReader;
    private readonly IVideoMetadataReader _videoMetadataReader;
    private readonly IVideoPlaybackItemFactory _playbackItemFactory;
    private readonly DispatcherQueueTimer _timer;
    private readonly SlideShowSequencer _sequencer;

    private IReadOnlyList<MediaItem> _playlist = [];

    /// <summary>装载序号：快速翻页时旧条目的异步装载完成不得覆盖新条目的显示。</summary>
    private int _loadSequence;

    /// <summary>本次条目切换是否已请求过转场，防止重复触发。</summary>
    private bool _transitionRequested;

    /// <summary>当前视频播放项；随切换、翻页、停止与销毁一并 Dispose。</summary>
    private VideoPlaybackItem? _playback;

    /// <summary>当前条目视频装载是否失败：失败时保持计时按间隔跳过，避免放映卡死。</summary>
    private bool _isVideoLoadFailed;

    [ObservableProperty]
    private MediaItem? _currentItem;

    [ObservableProperty]
    private BitmapImage? _sourceImage;

    /// <summary>上一帧：切换条目时留存，供转场动画播完前继续显示，由 CompleteTransition 清空。</summary>
    [ObservableProperty]
    private BitmapImage? _previousImage;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private int _rotationDegrees;

    /// <summary>当前条目是否为视频：页面据此切换图片层与视频层的显示。</summary>
    [ObservableProperty]
    private bool _isCurrentVideo;

    /// <summary>当前视频可直接赋给 MediaPlayer.Source 的播放项；非视频条目为 null。</summary>
    [ObservableProperty]
    private MediaPlaybackItem? _currentPlaybackItem;

    /// <summary>放映实际显示的图像：全图就绪前继续显示上一帧，避免切换条目时闪现背景。</summary>
    public BitmapImage? DisplayImage => SourceImage ?? PreviousImage;

    /// <summary>当前放映位置的可读文本。</summary>
    public string PositionText => _playlist.Count == 0
        ? "无文件"
        : $"{_sequencer.Current + 1} / {_playlist.Count}";

    /// <summary>是否可切到上一张。</summary>
    public bool CanGoPrevious => _sequencer.Current > 0;

    /// <summary>是否可切到下一张（列表语义；随机序由序列器保证可循环）。</summary>
    public bool CanGoNext => _sequencer.Current < _playlist.Count - 1;

    /// <summary>当前放映顺序；由外壳经 ApplySettings 推送。</summary>
    public SlideShowPlayOrder PlayOrder { get; private set; } = SlideShowPlayOrder.List;

    /// <summary>当前切换方式；由外壳经 ApplySettings 推送。</summary>
    public SlideShowTransitionMode Transition { get; private set; } = SlideShowTransitionMode.Slide;

    /// <summary>放映中的视频是否静音；由外壳经 ApplySettings 推送。</summary>
    public bool IsVideoMuted { get; private set; } = true;

    /// <summary>新帧已可显示、可以播放转场动画时触发；页面播完动画后必须回调 CompleteTransition。</summary>
    public event EventHandler? TransitionRequested;

    /// <summary>初始化放映视图模型。</summary>
    /// <param name="metadataReader">EXIF 方向读取器，用于图片朝向校正。</param>
    /// <param name="videoMetadataReader">视频元数据读取器，用于按编码选择解码后端。</param>
    /// <param name="playbackItemFactory">视频播放项工厂（FFmpeg 优先 + 系统解码回退）。</param>
    /// <param name="dispatcherQueue">UI 线程队列；测试注入用，缺省取当前线程。</param>
    public SlideShowViewModel(
        IImageMetadataReader metadataReader,
        IVideoMetadataReader videoMetadataReader,
        IVideoPlaybackItemFactory playbackItemFactory,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(videoMetadataReader);
        ArgumentNullException.ThrowIfNull(playbackItemFactory);

        _metadataReader = metadataReader;
        _videoMetadataReader = videoMetadataReader;
        _playbackItemFactory = playbackItemFactory;

        var queue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _timer = queue.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Tick += OnTimerTick;
        _timer.Interval = TimeSpan.FromSeconds(5);

        _sequencer = new SlideShowSequencer(PlayOrder);
    }

    /// <summary>按最新设置应用放映间隔、播放顺序、切换方式与视频静音策略。</summary>
    /// <remarks>
    /// 先停表再改间隔、改完按原状态续跑：放映途中改设置不会打断放映；
    /// 顺序变更即重洗，随机序列以当前条目为起点，放映不会跳条目。
    /// </remarks>
    /// <param name="settings">当前设置快照。</param>
    public void ApplySettings(AppSettings settings)
    {
        var wasPlaying = IsPlaying;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(settings.SlideShowIntervalSeconds);

        if (wasPlaying)
        {
            _timer.Start();
        }

        PlayOrder = settings.SlideShowOrder;
        Transition = settings.SlideShowTransition;
        IsVideoMuted = settings.SlideShowVideoMuted;
        _sequencer.Reshuffle();
    }

    /// <summary>转场动画播完的回调：清掉留存的旧帧，避免双层位图长期驻留内存。</summary>
    public void CompleteTransition()
    {
        if (PreviousImage is null)
        {
            return;
        }

        PreviousImage = null;
        OnPropertyChanged(nameof(DisplayImage));
    }

    /// <summary>装载放映列表并从指定条目开始。</summary>
    /// <param name="items">放映候选列表（可为图片与视频混合）。</param>
    /// <param name="startItem">起始条目；在过滤后的列表中按 Id 定位，找不到则从首个条目开始。</param>
    /// <param name="includeVideos">是否包含视频条目。</param>
    public async Task LoadPlaylistAsync(IReadOnlyList<MediaItem> items, MediaItem? startItem, bool includeVideos)
    {
        ArgumentNullException.ThrowIfNull(items);

        Stop();

        var playlist = includeVideos
            ? items.ToList()
            : items.Where(i => i.Kind != MediaKind.Video).ToList();

        _playlist = playlist;

        var startIndex = startItem is null
            ? 0
            : Math.Max(0, playlist.FindIndex(i => i.Id == startItem.Id));

        _sequencer.Reset(_playlist.Count, startIndex);

        if (_playlist.Count == 0)
        {
            CurrentItem = null;
            SourceImage = null;
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>装载当前条目。</summary>
    public async Task LoadCurrentAsync()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        // 旧帧留存到转场播完；上一条目为视频时显示链为空，留存亦为空，
        // 页面将走「单帧淡入」而不是交叉转场（视频无帧可留存，退场即黑场过渡）。
        PreviousImage = DisplayImage;
        _transitionRequested = false;
        RotationDegrees = 0;
        ReleasePlayback();

        CurrentItem = _playlist[_sequencer.Current];
        NotifyPositionChanged();

        var sequence = ++_loadSequence;

        if (CurrentItem.Kind == MediaKind.Video)
        {
            await LoadVideoAsync(CurrentItem, sequence);
        }
        else
        {
            await LoadImageAsync(CurrentItem, sequence);
        }

        SyncTimer();
    }

    /// <summary>切到上一张（手动跳转，列表式移动）。</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    public async Task GoPreviousAsync()
    {
        if (!_sequencer.TryMove(-1))
        {
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>切到下一张（手动跳转，列表式移动）。</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    public async Task GoNextAsync()
    {
        if (!_sequencer.TryMove(1))
        {
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>开始放映。</summary>
    [RelayCommand(CanExecute = nameof(HasMultipleItems))]
    public void Start()
    {
        if (!HasMultipleItems)
        {
            return;
        }

        IsPlaying = true;

        // 每次起播重新洗牌：从当前条目开始一轮全新随机序列（列表序不受影响）。
        _sequencer.Reshuffle();
        SyncTimer();
    }

    /// <summary>停止放映。</summary>
    public void Stop()
    {
        IsPlaying = false;
        _timer.Stop();
    }

    /// <summary>视频播放结束（或失败）的回调：放映中推进到下一张，暂停时停在当前条目。</summary>
    public void NotifyVideoEnded()
    {
        if (!IsPlaying)
        {
            return;
        }

        _ = AdvanceAfterVideoAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        ReleasePlayback();
    }

    private bool HasMultipleItems => _playlist.Count > 1;

    /// <summary>按放映状态同步计时器：放映中且当前条目为图片（或视频装载失败）时按间隔推进，
    /// 视频装载成功则停表改由播放结束事件驱动。</summary>
    private void SyncTimer()
    {
        _timer.Stop();

        if (IsPlaying && (!IsCurrentVideo || _isVideoLoadFailed))
        {
            _timer.Start();
        }
    }

    /// <summary>视频播完后的推进：经序列器自动推进语义（列表序走完停、随机序循环）。</summary>
    private async Task AdvanceAfterVideoAsync()
    {
        if (!_sequencer.TryAdvance())
        {
            Stop();
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>释放当前视频播放项；FFmpeg 的解码上下文与文件句柄不会因重写 Source 而回收。</summary>
    private void ReleasePlayback()
    {
        _playback?.Dispose();
        _playback = null;
        CurrentPlaybackItem = null;
        IsCurrentVideo = false;
        _isVideoLoadFailed = false;
    }

    /// <summary>在新帧可显示时请求一次转场。</summary>
    private void RequestTransition()
    {
        if (_transitionRequested || PreviousImage is null || DisplayImage is null)
        {
            return;
        }

        if (ReferenceEquals(DisplayImage, PreviousImage))
        {
            return;
        }

        _transitionRequested = true;
        TransitionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>放映计时到点：经序列器自动推进；列表序走完即停止，随机序由序列器保证循环。</summary>
    private async void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_sequencer.TryAdvance())
        {
            Stop();
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>装载当前视频：读元数据定解码后端，经工厂创建播放项后通知页面换源起播。</summary>
    private async Task LoadVideoAsync(MediaItem item, int sequence)
    {
        IsCurrentVideo = true;
        SourceImage = null;

        try
        {
            var metadata = await _videoMetadataReader.ReadAsync(item.Path);

            if (sequence != _loadSequence)
            {
                return;
            }

            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var playback = await _playbackItemFactory.CreateAsync(file, metadata);

            if (sequence != _loadSequence)
            {
                playback.Dispose();
                return;
            }

            _playback = playback;
            _isVideoLoadFailed = false;
            CurrentPlaybackItem = playback.Item;

            // 视频无「解码就绪」事件：换源即可显示，直接请求转场（页面做进场淡入）。
            if (!_transitionRequested)
            {
                _transitionRequested = true;
                TransitionRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException
                                      or NotSupportedException or InvalidOperationException)
        {
            Diagnostics.Log($"SLIDESHOW|VIDEOFAIL|{ex.GetType().Name}|{ex.HResult}");

            if (sequence == _loadSequence)
            {
                // 装载失败：保持计时运行，放映中按间隔跳过该条目，不中断放映。
                _isVideoLoadFailed = true;
                CurrentPlaybackItem = null;
            }
        }
    }

    private async Task LoadImageAsync(MediaItem item, int sequence)
    {
        IsCurrentVideo = false;
        SourceImage = null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            if (sequence != _loadSequence)
            {
                return;
            }

            SourceImage = bitmap;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            Diagnostics.Log($"SLIDESHOW|LOADFAIL|{ex.GetType().Name}|{ex.HResult}");

            if (sequence == _loadSequence)
            {
                SourceImage = null;
            }
        }

        if (sequence != _loadSequence)
        {
            return;
        }

        await ApplyExifOrientationAsync(item);
    }

    /// <summary>读取 EXIF 方向并校正显示角度；失败仅影响朝向，不中断放映。</summary>
    private async Task ApplyExifOrientationAsync(MediaItem item)
    {
        try
        {
            var metadata = await _metadataReader.ReadAsync(item.Path);

            if (metadata?.Orientation is { } orientation)
            {
                RotationDegrees = orientation switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0
                };
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"SLIDESHOW|EXIF|{ex.GetType().Name}|{ex.HResult}");
        }
    }

    private void NotifyPositionChanged()
    {
        OnPropertyChanged(nameof(PositionText));
        GoPreviousCommand.NotifyCanExecuteChanged();
        GoNextCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
}
