/**
 * 幻灯片放映代码后置。
 * 职责：设置数据上下文与焦点、处理键盘快捷键（翻页 / 暂停 / 退出），
 *      播放首帧入场淡入与关闭淡出动画，承担底部工具栏显隐调度与转场动画播放；
 *      创建 MediaPlayer 注入视频帧层并把播放结束 / 失败桥接为视图模型推进信号；
 *      创建背景音乐播放器并按「静音播放开关 × 背景音乐模式 × 音轨检测」落地音频策略。
 * 复用约定：视图模型由依赖注入提供，转场动画经 TransitionAnimationFactory 构造；
 *          背景音乐候选经 IMusicLibraryService 随机选曲、经 IVideoPlaybackItemFactory 解码
 *          （与 Short 页同一链路，音量/换曲模式保持一致）；
 *          页面由独立放映窗口（SlideShowWindow）承载，关闭统一经 Owner.Close() 收口，
 *          Closed 摘除内容后 Unloaded 负责清理。
 * 关键约束：动画目标直接取元素对象而非 TargetName（namescope 解析失败即静默无动画），
 *          入场/关闭/转场动画每轮 Begin 前必须 Stop 旧实例并复位起始值
 *          （FillBehavior 默认 HoldEnd 会保留终值且钉住属性）；
 *          MediaPlayer 必须懒创建（应用启动阶段构造会触发 WinRT 异常），且在 Unloaded
 *          释放，否则解码器不回收反复放映内存持续增长；
 *          转场请求发自解码 await 之后的线程，必须切回 UI 线程播放；
 *          音频策略：静音播放关闭时放映完全无声；混合模式下有音轨视频播放自身音频
 *          并压制背景音乐（避免混音打架），视频结束或切走后恢复；
 *          背景音乐跟随放映播放态（暂停即停）；
 *          画面扩大动画仅对图片生效（时长对齐放映间隔），与转场动画分层互扰需复位；
 *          每轮转场记录诊断日志（模式 / 旧帧 / 条目类型），供排查「切换方式未生效」；
 *          工具栏淡出计时在指针悬停于工具栏上时必须暂停，否则无法点击栏内按钮。
 */

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Services;
using Pixbian.ViewModels;
using Windows.Media.Playback;
using Windows.System;
using Windows.Storage;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Pixbian.Views;

/// <summary>幻灯片放映页。</summary>
public sealed partial class SlideShowPage : Page, IDisposable
{
    /// <summary>首帧入场淡入时长。</summary>
    private static readonly Duration EntryDuration = new(TimeSpan.FromMilliseconds(250));

    /// <summary>关闭时内容淡出时长。</summary>
    private static readonly Duration CloseFadeDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>工具栏淡入淡出时长。</summary>
    private static readonly Duration ToolbarFadeDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>转场动画时长。</summary>
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(280));

    /// <summary>工具栏无操作自动淡出的时长（毫秒）。</summary>
    private const int ToolbarAutoHideMilliseconds = 3000;

    private readonly IMusicLibraryService _musicLibrary;
    private readonly IVideoPlaybackItemFactory _playbackItemFactory;
    private readonly DispatcherQueueTimer _toolbarHideTimer;

    private bool _isToolbarVisible;
    private bool _hasEntryAnimationPlayed;
    private bool _isClosing;

    /// <summary>关闭淡出动画实例；HoldEnd 会把根层 Opacity 钉在 0，下次打开前必须 Stop。</summary>
    private Storyboard? _closeStoryboard;

    /// <summary>上一轮转场动画实例；HoldEnd 会钉住帧透明度，每轮 Begin 前必须 Stop。</summary>
    private Storyboard? _transitionStoryboard;

    /// <summary>首帧入场动画实例；HoldEnd 会钉住显示帧透明度，BeginOpen 复位前必须 Stop。</summary>
    private Storyboard? _entryStoryboard;

    /// <summary>画面扩大动画实例；切换条目或模式变更时必须 Stop 复位缩放。</summary>
    private Storyboard? _zoomStoryboard;

    /// <summary>视频播放器；懒创建（首个视频条目时），Unloaded 释放。</summary>
    private MediaPlayer? _player;

    /// <summary>当前视频是否含音轨（换源时检测），混合模式据此分派原声或背景音乐。</summary>
    private bool _currentVideoHasAudio;

    /// <summary>截取片段终点计时器（一次性）：到点推进到下一张，与 MediaEnded 互为兜底。</summary>
    private DispatcherQueueTimer? _segmentTimer;

    /// <summary>计时器武装时对应的播放项：到点时校验条目未变，防止切换后误推一张。</summary>
    private MediaPlaybackItem? _segmentArmedItem;

    /// <summary>背景音乐播放器；懒创建（策略首次要求播放时）。</summary>
    private MediaPlayer? _bgmPlayer;

    /// <summary>当前背景音乐播放项；换曲与释放时必须一并 Dispose。</summary>
    private VideoPlaybackItem? _bgmPlayback;

    /// <summary>背景音乐是否已被策略要求播放过（区分首次起播与暂停恢复）。</summary>
    private bool _isBgmStarted;

    /// <summary>初始化幻灯片放映页。</summary>
    /// <param name="viewModel">放映视图模型，由依赖注入提供。</param>
    /// <param name="musicLibrary">音乐库服务，提供背景音乐候选曲目。</param>
    /// <param name="playbackItemFactory">视频播放项工厂，背景音乐与视频共用同一解码链路。</param>
    public SlideShowPage(
        SlideShowViewModel viewModel,
        IMusicLibraryService musicLibrary,
        IVideoPlaybackItemFactory playbackItemFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(musicLibrary);
        ArgumentNullException.ThrowIfNull(playbackItemFactory);

        ViewModel = viewModel;
        _musicLibrary = musicLibrary;
        _playbackItemFactory = playbackItemFactory;
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnKeyDown;

        // 本页与视图模型同为 DI 单例、生命周期一致，故不在 Unloaded 里退订；
        // 若日后续任一方改为瞬态，必须在此配对退订，否则页面实例会被事件长期持有。
        ViewModel.TransitionRequested += OnTransitionRequested;
        ViewModel.AudioPolicyChanged += OnAudioPolicyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        _toolbarHideTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toolbarHideTimer.Interval = TimeSpan.FromMilliseconds(ToolbarAutoHideMilliseconds);
        _toolbarHideTimer.IsRepeating = false;
        _toolbarHideTimer.Tick += (_, _) => HideToolbar();

        _segmentTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _segmentTimer.IsRepeating = false;
        _segmentTimer.Tick += OnSegmentTimerTick;
    }

    /// <summary>放映视图模型。</summary>
    public SlideShowViewModel ViewModel { get; }

    /// <summary>承载本页的放映窗口；关闭放映统一经它收口（Close 触发本页 Unloaded 清理）。</summary>
    public SlideShowWindow? Owner { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 获得焦点后键盘快捷键（翻页、空格暂停）才会命中本页面。
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面必须停止放映并释放视频与背景音乐资源，否则后台持续触发切换、解码器不回收。
        Dispose();
        _toolbarHideTimer.Stop();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ViewModel.Dispose();
        VideoFrameElement.SetMediaPlayer(null);

        if (_player is not null)
        {
            _player.MediaEnded -= OnPlayerMediaEnded;
            _player.MediaFailed -= OnPlayerMediaFailed;
            _player.MediaOpened -= OnPlayerMediaOpened;
            _player.Dispose();
            _player = null;
        }

        _segmentTimer?.Stop();
        _segmentArmedItem = null;

        StopBgm();

        if (_bgmPlayer is not null)
        {
            _bgmPlayer.MediaEnded -= OnBgmMediaEnded;
            _bgmPlayer.MediaFailed -= OnBgmMediaFailed;
            _bgmPlayer.Dispose();
            _bgmPlayer = null;
        }

        _isBgmStarted = false;
    }

    private void OnPlayToggleChecked(object sender, RoutedEventArgs e) => ViewModel.Start();

    private void OnPlayToggleUnchecked(object sender, RoutedEventArgs e) => ViewModel.Stop();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithFade();

    /// <summary>
    /// 关闭放映：内容整体渐隐，完成后关闭窗口；关闭动画进行中忽略重复触发。
    /// </summary>
    private void CloseWithFade()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _toolbarHideTimer.Stop();

        // 不设 From：从当前透明度开始。HoldEnd 会在播完后把根层 Opacity 钉在 0——
        // 页面为跨窗口复用的单例，该 storyboard 实例必须存字段，供下次 BeginOpen 时 Stop 清除残留。
        var storyboard = new Storyboard { Duration = CloseFadeDuration };
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(RootLayer, "Opacity", null, 0, CloseFadeDuration));

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            Owner?.Close();
        }

        storyboard.Completed += OnCompleted;
        _closeStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>打开放映：复位状态并重聚焦点，等待首帧就绪播放入场淡入。</summary>
    public void BeginOpen()
    {
        _hasEntryAnimationPlayed = false;

        // 页面是跨窗口复用的单例：上一次关闭淡出的 HoldEnd（根层 Opacity=0）与关闭中
        // 标志都会残留，不复位则新窗口整层透明且 Esc 无法关闭。
        // HoldEnd 生效时本地赋值无效，必须先 Stop 旧 storyboard 清除其钉住效果。
        _closeStoryboard?.Stop();
        _closeStoryboard = null;
        _isClosing = false;
        RootLayer.Opacity = 1;

        // 入场/转场动画的 HoldEnd 都会钉住显示帧透明度，复位前必须先解除。
        _entryStoryboard?.Stop();
        _entryStoryboard = null;
        _transitionStoryboard?.Stop();
        _transitionStoryboard = null;

        // 工具栏恢复默认隐藏态。
        _isToolbarVisible = false;
        ToolbarRoot.Opacity = 0;
        ToolbarRoot.IsHitTestVisible = false;
        _toolbarHideTimer.Stop();
        DisplayFrameElement.Opacity = 0;
        PreviousFrameElement.Opacity = 1;
        PreviousFrameTransform.X = 0;
        DisplayFrameTransform.X = 0;
        VideoFrameElement.Opacity = 1;
        VideoFrameTransform.X = 0;
        UpdateFailedHint();

        // 新窗口激活后焦点可能落在别处，重聚本页保证键盘快捷键可用。
        Focus(FocusState.Programmatic);
    }

    /// <summary>首帧入场：图像淡入，每次打开只播一次。</summary>
    private void PlayEntryAnimation()
    {
        _hasEntryAnimationPlayed = true;

        var storyboard = new Storyboard { Duration = EntryDuration };
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(DisplayFrameElement, "Opacity", 0, 1, EntryDuration));

        _entryStoryboard?.Stop();
        _entryStoryboard = storyboard;
        storyboard.Begin();

        StartZoomAnimationIfNeeded();
    }

    /// <summary>
    /// 画面扩大动画：图片条目在适应大小基础上从 1.0 匀速放大到 1.2（时长对齐放映间隔）；
    /// 视频条目与「无」模式复位缩放不播。仅作用于显示帧，旧帧退场保持原样。
    /// </summary>
    private void StartZoomAnimationIfNeeded()
    {
        // HoldEnd 会把缩放钉在上一轮终值，每轮先 Stop 并复位。
        _zoomStoryboard?.Stop();
        _zoomStoryboard = null;
        DisplayFrameScale.ScaleX = 1;
        DisplayFrameScale.ScaleY = 1;

        if (ViewModel.IsCurrentVideo
            || ViewModel.AnimationMode != SlideShowAnimationMode.Zoom
            || ViewModel.IntervalSeconds <= 0)
        {
            return;
        }

        var duration = new Duration(TimeSpan.FromSeconds(ViewModel.IntervalSeconds));
        var storyboard = new Storyboard { Duration = duration };

        // 线性推进（不设缓动函数）：Ken Burns 匀速放大才自然。
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(DisplayFrameScale, "ScaleX", 1.0, 1.2, duration));
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(DisplayFrameScale, "ScaleY", 1.0, 1.2, duration));

        _zoomStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>视图模型属性变化：首帧就绪播入场动画、更新失败提示、同步视频源与放映播放态。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.DisplayImage):
                if (!_hasEntryAnimationPlayed)
                {
                    if (ViewModel.DisplayImage is not null)
                    {
                        PlayEntryAnimation();
                    }
                }
                else
                {
                    UpdateFailedHint();
                }

                break;

            case nameof(ViewModel.CurrentPlaybackItem):
                ApplyPlaybackSource();
                break;

            case nameof(ViewModel.IsPlaying):
                // 暂停/继续：背景音乐跟随放映播放态（视频静音策略不受影响）。
                ComputeAudioState();
                break;
        }
    }

    /// <summary>把视图模型的播放项变化落到播放器：换源起播或清源退出视频层，并重算音频策略。</summary>
    private void ApplyPlaybackSource()
    {
        var playback = ViewModel.CurrentVideoPlayback;

        if (playback is null)
        {
            // 视频退场即黑场过渡（视频无帧可留存），清源并隐藏视频层。
            if (_player is not null)
            {
                _player.Source = null;
            }

            VideoFrameElement.Visibility = Visibility.Collapsed;
            _currentVideoHasAudio = false;
            ComputeAudioState();
            return;
        }

        VideoFrameElement.Visibility = Visibility.Visible;

        // 先置透明防闪帧：淡入动画要到 BeginTransition（下一轮消息）才接管透明度。
        VideoFrameElement.Opacity = 0;

        var player = EnsurePlayer();
        player.Source = playback.Item;
        player.Play();

        // 换源后音轨解析已就绪（FFmpeg 源解析完成、系统源已连接播放器），此时检测最可靠。
        _currentVideoHasAudio = playback.HasAudio();
        ArmSegmentTimer(playback.Item);
        ComputeAudioState();
    }

    /// <summary>按视图模型的截取区间武装终点计时器；全播（区间为空）时仅解除旧计时。</summary>
    private void ArmSegmentTimer(MediaPlaybackItem item)
    {
        _segmentTimer?.Stop();
        _segmentArmedItem = null;

        if (ViewModel.CurrentVideoSegmentStart is not { } start
            || ViewModel.CurrentVideoSegmentEnd is not { } end)
        {
            return;
        }

        var length = end - start;

        if (length <= TimeSpan.Zero)
        {
            return;
        }

        // 到点时校验条目未变：视频若提前自然结束已触发推进，不得重复跳张。
        _segmentArmedItem = item;
        _segmentTimer!.Interval = length;
        _segmentTimer!.Start();

        Diagnostics.Log($"SLIDESHOW|SEGMENT|start={start.TotalSeconds:F0}|end={end.TotalSeconds:F0}");
    }

    /// <summary>截取片段到点：条目未变时推进到下一张。</summary>
    private void OnSegmentTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_segmentArmedItem is not null
            && ReferenceEquals(ViewModel.CurrentPlaybackItem, _segmentArmedItem))
        {
            Diagnostics.Log("SLIDESHOW|SEGMENTEND");
            ViewModel.NotifyVideoEnded();
        }
    }

    /// <summary>媒体打开后跳到截取片段起点；全播时无操作。</summary>
    private void OnPlayerMediaOpened(MediaPlayer sender, object args)
    {
        if (ViewModel.CurrentVideoSegmentStart is { } start && start > TimeSpan.Zero)
        {
            sender.PlaybackSession.Position = start;
        }
    }

    /// <summary>懒创建播放器并注入视频帧层；必须在首个视频条目时才构造，规避启动期 WinRT 异常。</summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player is not null)
        {
            return _player;
        }

        _player = new MediaPlayer();
        _player.MediaEnded += OnPlayerMediaEnded;
        _player.MediaFailed += OnPlayerMediaFailed;
        _player.MediaOpened += OnPlayerMediaOpened;
        VideoFrameElement.SetMediaPlayer(_player);

        return _player;
    }

    /// <summary>视频播完或播放失败：桥接为视图模型的推进信号（失败按播完处理，不中断放映）。</summary>
    private void OnPlayerMediaEnded(MediaPlayer sender, object args)
    {
        Diagnostics.Log("SLIDESHOW|VIDEOENDED");
        ViewModel.NotifyVideoEnded();
    }

    private void OnPlayerMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Diagnostics.Log($"SLIDESHOW|VIDEOMEDIAFAIL|{args.Error}|{args.ErrorMessage}");
        ViewModel.NotifyVideoEnded();
    }

    /// <summary>音频策略变化（设置变更推送）：重算当前条目的视频静音与背景音乐启停。</summary>
    private void OnAudioPolicyChanged(object? sender, EventArgs e) => ComputeAudioState();

    /// <summary>
    /// 按「静音播放开关 × 背景音乐模式 × 音轨检测 × 放映播放态」推导音频状态并落地：
    /// 开关关闭或静音模式时放映完全无声；混合模式有音轨视频播原声并压制背景音乐；
    /// 音乐库模式视频一律静音配背景音乐。背景音乐跟随放映播放态（暂停即停）。
    /// </summary>
    private void ComputeAudioState()
    {
        var videoMuted = true;
        var bgmDesired = false;

        if (ViewModel.IsPlaying && ViewModel.IsSilentPlayback)
        {
            switch (ViewModel.BackgroundMusic)
            {
                case BackgroundMusicMode.Mixed:
                    if (_currentVideoHasAudio)
                    {
                        // 有音轨视频：播放自身音频，压制背景音乐避免混音打架。
                        videoMuted = false;
                    }
                    else
                    {
                        bgmDesired = true;
                    }

                    break;

                case BackgroundMusicMode.MusicLibrary:
                    bgmDesired = true;
                    break;

                case BackgroundMusicMode.Muted:
                default:
                    break;
            }
        }

        ApplyAudioState(videoMuted, bgmDesired);
    }

    /// <summary>把音频状态落到播放器：视频静音即时生效，背景音乐按需起播 / 恢复 / 暂停。</summary>
    private void ApplyAudioState(bool videoMuted, bool bgmDesired)
    {
        if (_player is not null)
        {
            _player.IsMuted = videoMuted;
        }

        if (bgmDesired)
        {
            var bgmPlayer = EnsureBgmPlayer();
            bgmPlayer.Volume = ViewModel.BgmVolume;

            if (_isBgmStarted)
            {
                bgmPlayer.Play();
            }
            else
            {
                _isBgmStarted = true;
                _ = PlayNextBgmAsync();
            }
        }
        else if (_isBgmStarted)
        {
            _bgmPlayer?.Pause();
        }
    }

    /// <summary>懒创建背景音乐播放器；策略首次要求播放时才构造。</summary>
    private MediaPlayer EnsureBgmPlayer()
    {
        if (_bgmPlayer is not null)
        {
            return _bgmPlayer;
        }

        _bgmPlayer = new MediaPlayer();
        _bgmPlayer.MediaEnded += OnBgmMediaEnded;
        _bgmPlayer.MediaFailed += OnBgmMediaFailed;

        return _bgmPlayer;
    }

    /// <summary>播放下一首随机背景音乐；候选池为空或解码失败仅留痕，不中断放映。</summary>
    private async Task PlayNextBgmAsync()
    {
        var trackPath = _musicLibrary.TakeRandomTrack();

        if (string.IsNullOrEmpty(trackPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(trackPath);
            _bgmPlayback?.Dispose();
            _bgmPlayback = await _playbackItemFactory.CreateAsync(file, null);

            var bgmPlayer = EnsureBgmPlayer();
            bgmPlayer.Volume = ViewModel.BgmVolume;
            bgmPlayer.Source = _bgmPlayback.Item;
            bgmPlayer.Play();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException
                                      or NotSupportedException)
        {
            Diagnostics.Log($"SLIDESHOW|BGMFAIL|{ex.GetType().Name}|{ex.HResult}");
        }
    }

    /// <summary>背景音乐曲终：策略仍要求播放时随机续播下一首（无限循环）。</summary>
    private void OnBgmMediaEnded(MediaPlayer sender, object args)
    {
        if (_isBgmStarted)
        {
            _ = PlayNextBgmAsync();
        }
    }

    private void OnBgmMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        Diagnostics.Log($"SLIDESHOW|BGMMEDIAFAIL|{args.Error}|{args.ErrorMessage}");

    /// <summary>停止背景音乐（保留播放器实例供本页复用），并释放当前曲目解码上下文。</summary>
    private void StopBgm()
    {
        var stale = _bgmPlayback;
        _bgmPlayback = null;

        if (_bgmPlayer is not null)
        {
            _bgmPlayer.Pause();
            _bgmPlayer.Source = null;
        }

        stale?.Dispose();
    }

    /// <summary>失败提示仅在首帧已就绪过、且当前无任何可显示图像时出现（加载期间静默）。</summary>
    private void UpdateFailedHint() =>
        FailedHint.Visibility = !_hasEntryAnimationPlayed || ViewModel.DisplayImage is not null
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>显示工具栏并重置自动淡出计时。</summary>
    private void ShowToolbar()
    {
        _isToolbarVisible = true;
        ToolbarRoot.IsHitTestVisible = true;

        var storyboard = new Storyboard { Duration = ToolbarFadeDuration };
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(ToolbarRoot, "Opacity", null, 1, ToolbarFadeDuration));
        storyboard.Begin();

        RestartToolbarHideTimer();
    }

    /// <summary>隐藏工具栏。</summary>
    private void HideToolbar()
    {
        if (!_isToolbarVisible)
        {
            return;
        }

        _isToolbarVisible = false;
        _toolbarHideTimer.Stop();
        ToolbarRoot.IsHitTestVisible = false;

        var storyboard = new Storyboard { Duration = ToolbarFadeDuration };
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(ToolbarRoot, "Opacity", null, 0, ToolbarFadeDuration));
        storyboard.Begin();
    }

    private void RestartToolbarHideTimer()
    {
        _toolbarHideTimer.Stop();
        _toolbarHideTimer.Start();
    }

    private void OnToolbarPointerEntered(object sender, PointerRoutedEventArgs e) => _toolbarHideTimer.Stop();

    private void OnToolbarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isToolbarVisible)
        {
            RestartToolbarHideTimer();
        }
    }

    /// <summary>点击画面任意处切换工具栏显隐；关闭只走 Esc 与右上角关闭按钮，防误触。</summary>
    private void OnFrameHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isToolbarVisible)
        {
            HideToolbar();
        }
        else
        {
            ShowToolbar();
        }
    }

    /// <summary>新帧可显示时由视图模型发起转场请求。</summary>
    private void OnTransitionRequested(object? sender, EventArgs e)
    {
        // 请求发自解码 await 之后，彼时可能已在线程池线程，动画必须由 UI 线程播放。
        DispatcherQueue.TryEnqueue(BeginTransition);
    }

    /// <summary>按当前设置播放一次转场动画。</summary>
    /// <remarks>
    /// 统一按切换方式设置分派（滑动 / 淡出），旧帧与新帧按条目类型选择目标元素；
    /// 无旧帧可对照（首次装载 / 旧视频退场）时新帧单帧淡入。
    /// 每轮 Begin 前必须 Stop 上一轮实例：HoldEnd 会钉住帧透明度，本地赋值无法覆盖。
    /// </remarks>
    private void BeginTransition()
    {
        var newIsVideo = ViewModel.IsCurrentVideo;
        var hasPreviousImage = ViewModel.PreviousImage is not null;

        // 诊断：排查「切换方式未生效」——确认到达转场的模式取值与分派路径。
        Diagnostics.Log(
            $"SLIDESHOW|TRANS|mode={ViewModel.Transition}|prev={hasPreviousImage}|video={newIsVideo}"
            + $"|item={ViewModel.CurrentItem?.Kind}");

        // 先 Stop 上一轮转场（HoldEnd 钉住的属性值只有 Stop 才能解除），再复位起始值。
        _transitionStoryboard?.Stop();
        _transitionStoryboard = null;

        PreviousFrameElement.Opacity = 1;
        DisplayFrameElement.Opacity = 1;
        PreviousFrameTransform.X = 0;
        DisplayFrameTransform.X = 0;
        VideoFrameElement.Opacity = 1;
        VideoFrameTransform.X = 0;

        DependencyObject newTarget = newIsVideo ? VideoFrameElement : DisplayFrameElement;
        TranslateTransform newTransform = newIsVideo ? VideoFrameTransform : DisplayFrameTransform;

        Storyboard storyboard;

        if (hasPreviousImage)
        {
            storyboard = ViewModel.Transition == SlideShowTransitionMode.Fade
                ? TransitionAnimationFactory.CreateFadeStoryboard(PreviousFrameElement, newTarget, TransitionDuration)
                : TransitionAnimationFactory.CreateSlideStoryboard(
                    PreviousFrameElement, PreviousFrameTransform, newTarget, newTransform, TransitionDuration);
        }
        else
        {
            storyboard = new Storyboard { Duration = TransitionDuration };
            storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(newTarget, "Opacity", 0, 1, TransitionDuration));
        }

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            ViewModel.CompleteTransition();
        }

        storyboard.Completed += OnCompleted;

        _transitionStoryboard = storyboard;
        storyboard.Begin();

        // 转场开始即启动画面扩大（若启用），时长对齐放映间隔，切换瞬间正好放大到终值。
        StartZoomAnimationIfNeeded();
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                await ViewModel.GoPreviousCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                await ViewModel.GoNextCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case VirtualKey.Space:
                if (ViewModel.IsPlaying)
                {
                    ViewModel.Stop();
                }
                else
                {
                    ViewModel.Start();
                }

                e.Handled = true;
                break;

            case VirtualKey.Escape:
                ViewModel.Stop();
                CloseWithFade();
                e.Handled = true;
                break;
        }
    }
}
