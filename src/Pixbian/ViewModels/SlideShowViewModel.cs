/**
 * 幻灯片放映视图模型。
 * 职责：管理放映列表与游标推进（列表 / 随机），按设置应用间隔、切换方式与视频策略，
 *      装载当前条目并经转场事件通知页面——图片条目只传递路径与 EXIF 角度，
 *      画面由页面侧的合成渲染器产出（模糊背景 + 照片合成为一帧）；
 *      视频条目经播放项工厂走解码管线并推送播放项。
 * 复用约定：游标与洗牌由 SlideShowSequencer 承担，本类不重复实现序列语义；
 *          视频播放项复用 IVideoPlaybackItemFactory（FFmpeg 优先 + 系统解码回退），
 *          解码后端按编码选择所需的元数据经 IVideoMetadataReader 读取；
 *          放映配置由外壳在设置变更时经 ApplySettings 推送，本类不反向依赖设置服务；
 *          页面播完转场动画必须回调 CompleteTransition，否则旧帧路径长期驻留。
 * 关键约束：装载序号必须防快速翻页错配——旧条目的异步装载完成不得覆盖新条目的显示；
 *          EXIF 方向必须早于转场请求发出，否则合成帧会以错误朝向绘制；
 *          无论是否有旧帧，图片装载完成都必须补发转场请求——首帧的画面合成
 *          也依赖该信号，若只在有旧帧时发出，首帧将永远得不到画面；
 *          视频条目不按间隔计时，由播放结束事件驱动推进，装载成功必须停表、
 *          失败须保持计时以按间隔跳过坏条目，否则放映会卡死或快速循环；
 *          播放项必须随切换、翻页、停止与销毁 Dispose，否则 FFmpeg 解码上下文与文件句柄滞留；
 *          停止放映与页面卸载时必须停定时器；事件在 await 之后的线程上发出，
 *          页面必须自行切回 UI 线程播动画。
 *          音频策略（静音播放开关 / 背景音乐模式）只在此存储与推送，视频静音与
 *          背景音乐启停由页面结合音轨检测结果落地——本类不持有任何播放器实例。
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
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
    private string? _sourcePath;

    /// <summary>上一帧路径：切换条目时留存，供转场动画播完前继续显示，由 CompleteTransition 清空。</summary>
    [ObservableProperty]
    private string? _previousPath;

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

    /// <summary>显示链派生属性随源路径变化联动通知，并在新帧可显示时发起转场请求。</summary>
    partial void OnSourcePathChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayPath));
        RequestTransition();
    }

    /// <summary>当前视频的播放项包装（含音轨检测）；在 CurrentPlaybackItem 变化通知发出前就绪，
    /// 页面收到通知后读取；非视频条目为 null。</summary>
    public VideoPlaybackItem? CurrentVideoPlayback => _playback;

    /// <summary>放映实际显示的图片路径：新帧就绪前继续沿用上一帧，避免切换条目时闪黑。</summary>
    public string? DisplayPath => SourcePath ?? PreviousPath;

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

    /// <summary>静音播放开关：是否启用背景音乐体系；关闭时放映完全无声。</summary>
    public bool IsSilentPlayback { get; private set; }

    /// <summary>是否播放完整视频；关闭时按截取片段策略播放。</summary>
    public bool IsFullVideoPlayback { get; private set; } = true;

    /// <summary>截取片段的时长上限（秒）；由外壳经 ApplySettings 推送。合法值取 ClipRangePlanner.PresetOptions。</summary>
    public int ClipPresetSeconds { get; private set; } = 60;

    /// <summary>当前视频的截取片段起点；全播为 null。</summary>
    public TimeSpan? CurrentVideoSegmentStart { get; private set; }

    /// <summary>当前视频的截取片段终点；全播为 null。</summary>
    public TimeSpan? CurrentVideoSegmentEnd { get; private set; }

    /// <summary>背景音乐模式；仅在静音播放开启时生效。</summary>
    public BackgroundMusicMode BackgroundMusic { get; private set; } = BackgroundMusicMode.Muted;

    /// <summary>背景音乐音量（0–1）。</summary>
    public double BgmVolume { get; private set; } = 0.8;

    /// <summary>是否启用画面动画（Ken Burns）；由外壳经 ApplySettings 推送。</summary>
    public bool IsAnimationEnabled { get; private set; } = true;

    /// <summary>放映间隔秒数；底部进度条按它计算图片的停留进度时长。</summary>
    public int IntervalSeconds { get; private set; } = 5;

    /// <summary>是否以当前照片的虚化放大图作为放映背景；由外壳经 ApplySettings 推送。</summary>
    public bool IsBlurBackdrop { get; private set; } = true;

    /// <summary>新帧已可显示、可以播放转场动画时触发；页面播完动画后必须回调 CompleteTransition。</summary>
    public event EventHandler? TransitionRequested;

    /// <summary>音频策略（静音播放开关 / 背景音乐模式 / 音量）变化时触发；
    /// 页面须重算当前条目的视频静音与背景音乐启停。</summary>
    public event EventHandler? AudioPolicyChanged;

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

    /// <summary>按最新设置应用放映间隔、播放顺序、切换方式与音频策略。</summary>
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
        IsSilentPlayback = settings.SlideShowSilentPlayback;
        IsFullVideoPlayback = settings.SlideShowFullVideoPlayback;
        ClipPresetSeconds = ClipRangePlanner.PresetOptions.Contains(settings.SlideShowClipPresetSeconds)
            ? settings.SlideShowClipPresetSeconds
            : 60;
        BackgroundMusic = settings.SlideShowBackgroundMusic;
        BgmVolume = Math.Clamp(settings.SlideShowBackgroundMusicVolume, 0, 1);
        IsAnimationEnabled = settings.SlideShowAnimationEnabled;
        IsBlurBackdrop = settings.SlideShowBlurBackdrop;
        IntervalSeconds = settings.SlideShowIntervalSeconds;
        _sequencer.Reshuffle();

        AudioPolicyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>转场动画播完的回调：清掉留存的旧帧路径，避免旧画面路径长期驻留。</summary>
    public void CompleteTransition()
    {
        if (PreviousPath is null)
        {
            return;
        }

        PreviousPath = null;
        OnPropertyChanged(nameof(DisplayPath));
    }

    /// <summary>装载放映列表并从指定条目开始；列表恒包含视频条目。</summary>
    /// <param name="items">放映候选列表（可为图片与视频混合）。</param>
    /// <param name="startItem">起始条目；在列表中按 Id 定位，找不到则从首个条目开始。</param>
    public async Task LoadPlaylistAsync(IReadOnlyList<MediaItem> items, MediaItem? startItem)
    {
        ArgumentNullException.ThrowIfNull(items);

        Stop();

        var playlist = items.ToList();

        _playlist = playlist;

        var startIndex = startItem is null
            ? 0
            : Math.Max(0, playlist.FindIndex(i => i.Id == startItem.Id));

        Diagnostics.Log(
            $"SLIDESHOW|PLAYLIST|total={items.Count}|start={startIndex}"
            + $"|order={PlayOrder}|interval={_timer.Interval.TotalSeconds}");

        _sequencer.Reset(_playlist.Count, startIndex);

        if (_playlist.Count == 0)
        {
            CurrentItem = null;
            SourcePath = null;
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

        // 旧帧路径留存到转场播完；上一条目为视频时显示链为空，留存亦为空，
        // 页面将走「单帧淡入」而不是交叉转场（视频无帧可留存，退场即黑场过渡）。
        PreviousPath = DisplayPath;
        _transitionRequested = false;
        RotationDegrees = 0;
        ReleasePlayback();

        CurrentItem = _playlist[_sequencer.Current];
        NotifyPositionChanged();

        var sequence = ++_loadSequence;

        Diagnostics.Log(
            $"SLIDESHOW|LOAD|index={_sequencer.Current}|kind={CurrentItem.Kind}"
            + $"|playing={IsPlaying}");

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

    /// <summary>视频结束推进进行中标记：片段计时器与 MediaEnded 可能先后到达，防止双推进跳张。</summary>
    private bool _isAdvancingAfterVideo;

    /// <summary>视频结束后的推进：经序列器自动推进语义（列表序走完停、随机序循环）。</summary>
    private async Task AdvanceAfterVideoAsync()
    {
        if (_isAdvancingAfterVideo)
        {
            return;
        }

        _isAdvancingAfterVideo = true;

        try
        {
            if (!_sequencer.TryAdvance())
            {
                Stop();
                return;
            }

            await LoadCurrentAsync();
        }
        finally
        {
            _isAdvancingAfterVideo = false;
        }
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

    /// <summary>
    /// 按完整视频开关与「片段区间」预设计算播放区间，裁决走 ClipRangePlanner（与片段界面同一规则）。
    /// 起点为零即整段播放语义：区间保持 null，交由播放结束事件驱动推进，
    /// 避免片段计时器与 MediaEnded 双触发造成跳张。
    /// </summary>
    /// <param name="metadata">视频元数据；提供总时长，缺失时退化为全播。</param>
    private void ComputeVideoSegment(VideoMetadata? metadata)
    {
        CurrentVideoSegmentStart = null;
        CurrentVideoSegmentEnd = null;

        if (IsFullVideoPlayback || metadata?.Duration is not { } duration || duration <= TimeSpan.Zero)
        {
            return;
        }

        var clip = ClipRangePlanner.Plan(duration, ClipPresetSeconds, Random.Shared);

        if (clip.Start > TimeSpan.Zero)
        {
            CurrentVideoSegmentStart = clip.Start;
            CurrentVideoSegmentEnd = clip.End;
        }
    }

    /// <summary>释放当前视频播放项；FFmpeg 的解码上下文与文件句柄不会因重写 Source 而回收。</summary>
    private void ReleasePlayback()
    {
        _playback?.Dispose();
        _playback = null;
        CurrentPlaybackItem = null;
        CurrentVideoSegmentStart = null;
        CurrentVideoSegmentEnd = null;
        IsCurrentVideo = false;
        _isVideoLoadFailed = false;
    }

    /// <summary>在新帧可显示时请求一次转场。</summary>
    private void RequestTransition()
    {
        if (_transitionRequested)
        {
            Diagnostics.Log("SLIDESHOW|TRANS|skip=requested");
            return;
        }

        if (PreviousPath is null)
        {
            Diagnostics.Log("SLIDESHOW|TRANS|skip=no-previous");
            return;
        }

        if (DisplayPath is null)
        {
            Diagnostics.Log("SLIDESHOW|TRANS|skip=no-display");
            return;
        }

        if (string.Equals(DisplayPath, PreviousPath, StringComparison.Ordinal))
        {
            Diagnostics.Log("SLIDESHOW|TRANS|skip=same-frame");
            return;
        }

        _transitionRequested = true;
        Diagnostics.Log("SLIDESHOW|TRANS|raised");
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
        SourcePath = null;

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

            ComputeVideoSegment(metadata);
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

    /// <summary>
    /// 装载当前图片：只读 EXIF 方向并把路径交给页面——画面由页面侧的合成渲染器
    /// 从路径直接解码产出（模糊背景 + 照片合成一帧），本类不再装载位图，
    /// 避免同一路径被解码两次。无论首帧与否都补发转场请求：首帧的画面合成同样依赖该信号。
    /// </summary>
    /// <param name="item">当前图片条目。</param>
    /// <param name="sequence">装载序号；回传时校验未变，防止错配覆盖。</param>
    private async Task LoadImageAsync(MediaItem item, int sequence)
    {
        IsCurrentVideo = false;
        SourcePath = null;

        // 方向先于转场请求发出：合成渲染器按此角度绘制，晚了就会画出错误朝向。
        RotationDegrees = await ReadOrientationAsync(item);

        if (sequence != _loadSequence)
        {
            return;
        }

        SourcePath = item.Path;

        if (!_transitionRequested)
        {
            _transitionRequested = true;
            Diagnostics.Log("SLIDESHOW|TRANS|raised");
            TransitionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>读取 EXIF 方向对应的显示角度；失败仅影响朝向，退化为 0 度，不中断放映。</summary>
    /// <param name="item">当前图片条目。</param>
    private async Task<int> ReadOrientationAsync(MediaItem item)
    {
        try
        {
            var metadata = await _metadataReader.ReadAsync(item.Path);

            return metadata?.Orientation switch
            {
                3 => 180,
                6 => 90,
                8 => 270,
                _ => 0
            };
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"SLIDESHOW|EXIF|{ex.GetType().Name}|{ex.HResult}");
            return 0;
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
