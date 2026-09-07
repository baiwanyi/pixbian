/**
 * 幻灯片放映代码后置。
 * 职责：设置数据上下文与焦点、处理键盘快捷键（翻页 / 暂停 / 退出），
 *      经合成渲染器为每张照片产出「虚化背景 + 照片」的舞台等大合成帧并承担呈现与转场动画，
 *      对合成帧施加 Ken Burns 画面动画（放大 / 缩小 / 平移，随机取一种），
 *      创建 MediaPlayer 注入视频帧层并把播放结束 / 失败桥接为视图模型推进信号，
 *      创建背景音乐播放器并按「静音播放开关 × 背景音乐模式 × 音轨检测」落地音频策略，
 *      驱动底部放映进度条（图片按间隔、视频按播放位置）。
 * 复用约定：视图模型由依赖注入提供，转场动画经 TransitionAnimationFactory 构造；
 *          合成帧经 SlideShowFrameRenderer 从照片路径直接解码产出（视图模型只传路径与角度）；
 *          背景音乐候选经 IMusicLibraryService 随机选曲、经 IVideoPlaybackItemFactory 解码；
 *          页面由独立放映窗口（SlideShowWindow）承载，关闭统一经 Owner.Close() 收口。
 * 关键约束：动画目标直接取元素对象而非 TargetName（namescope 解析失败即静默无动画），
 *          入场/关闭/转场动画每轮 Begin 前必须 Stop 旧实例并复位起始值
 *          （FillBehavior 默认 HoldEnd 会保留终值且钉住属性）；
 *          合成是异步的，呈现以呈现序号防错配——渲染期间已切到更新的条目则丢弃本轮结果；
 *          转场开始时须把显示帧的内容与运动终态（缩放 / 平移）移交给旧帧，
 *          否则旧帧会在切换瞬间缩回初始状态；
 *          合成帧铺满舞台且 Ken Burns 倍率恒 ≥ 1，边缘永远在视口外；
 *          同一层的缩放只能由一个动画源驱动，转场不缩放新帧（缩放全部交给画面动画）；
 *          ProgressBar.Value 影响布局，属依赖动画，必须显式 EnableDependentAnimation；
 *          MediaPlayer 必须懒创建（应用启动阶段构造会触发 WinRT 异常），且在 Unloaded 释放；
 *          转场请求发自解码 await 之后的线程，必须切回 UI 线程呈现；
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

    /// <summary>滑动转场时长：位移量大，过短会像抖动。</summary>
    private static readonly Duration SlideTransitionDuration = new(TimeSpan.FromMilliseconds(450));

    /// <summary>交叉淡入转场时长。</summary>
    private static readonly Duration FadeTransitionDuration = new(TimeSpan.FromMilliseconds(700));

    /// <summary>溶解转场时长：比交叉淡入更长，配合旧帧放大退场形成柔和切换。</summary>
    private static readonly Duration DissolveTransitionDuration = new(TimeSpan.FromMilliseconds(900));

    /// <summary>
    /// 画面动画时长；与放映间隔解耦——固定较慢的匀速速度，间隔调长调短都不改变观感。
    /// </summary>
    private static readonly Duration KenBurnsDuration = new(TimeSpan.FromSeconds(10));

    /// <summary>
    /// 画面缩放类动画的极端倍率：合成帧铺满舞台（1.0），放大到该值；缩小类反向播放。
    /// 合成帧边缘恒在视口外，幅度只受重采样爬行制约，15% 在 10 秒里运动感明显且不抖。
    /// </summary>
    private const double KenBurnsZoomScale = 1.15;

    /// <summary>画面平移类动画的固定放大倍率：合成帧放大 6% 提供平移余量，避免露出舞台底色。</summary>
    private const double KenBurnsPanScale = 1.06;

    /// <summary>
    /// 画面平移的单侧幅度占视口宽度的比例；须小于平移余量 (1.06-1)/2 = 3%，
    /// 否则合成帧平移时会把边缘推入视口。
    /// </summary>
    private const double KenBurnsPanRatio = 0.025;

    /// <summary>工具栏无操作自动淡出的时长（毫秒）。</summary>
    private const int ToolbarAutoHideMilliseconds = 3000;

    /// <summary>视频进度的轮询间隔（毫秒）；与补间时长一致，节拍之间连续无跳动。</summary>
    private const int ProgressPollMilliseconds = 200;

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

    /// <summary>画面动画（缩放 + 平移）实例；切换条目或模式变更时必须 Stop 复位。</summary>
    private Storyboard? _kenBurnsStoryboard;

    /// <summary>当前合成帧的 Ken Burns 运动形态；呈现时随机决定，画面动画按其初值推进到终值。</summary>
    private KenBurnsMotion _currentMotion = KenBurnsMotion.ZoomIn;

    /// <summary>当前运动形态的起始时刻；用于按线性插值计算运动进度（不依赖属性 getter 读动画值）。</summary>
    private DateTimeOffset _motionStartedUtc = DateTimeOffset.UtcNow;

    /// <summary>画面合成渲染器；持有 Win2D 设备，随页面释放。</summary>
    private readonly SlideShowFrameRenderer _frameRenderer = new();

    /// <summary>呈现序号；合成是异步的，回传时据此丢弃已被后续条目取代的结果。</summary>
    private int _presentSequence;

    /// <summary>图片停留进度动画；暂停放映时 Pause，切换条目时重建。</summary>
    private Storyboard? _progressStoryboard;

    /// <summary>视频播放进度的轮询计时器；仅视频条目运行。</summary>
    private readonly DispatcherQueueTimer _progressTimer;

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

        _progressTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += OnProgressTimerTick;

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
        _frameRenderer.Dispose();
        StopProgress();
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

    /// <summary>打开放映：复位状态并重聚焦点，等待首帧合成完成播放入场淡入。</summary>
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

        _entryStoryboard?.Stop();
        _entryStoryboard = null;
        _transitionStoryboard?.Stop();
        _transitionStoryboard = null;
        _kenBurnsStoryboard?.Stop();
        _kenBurnsStoryboard = null;
        StopProgress();

        // 工具栏恢复默认隐藏态。
        _isToolbarVisible = false;
        ToolbarRoot.Opacity = 0;
        ToolbarRoot.IsHitTestVisible = false;
        _toolbarHideTimer.Stop();

        DisplayFrameElement.Opacity = 0;
        PreviousFrameElement.Opacity = 1;
        VideoFrameElement.Opacity = 1;
        VideoFrameTransform.X = 0;
        DisplayFrameElement.Source = null;
        PreviousFrameElement.Source = null;
        ResetDisplayFrameTransforms();

        // 重新打开放映：先给全屏装载反馈，首帧合成完成后由呈现流程撤下。
        LoadingLayer.Visibility = Visibility.Visible;
        UpdateFailedHint();

        // 新窗口激活后焦点可能落在别处，重聚本页保证键盘快捷键可用。
        Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 复位显示帧的画面变换。旧帧的变换是移交来的运动终态，**绝不能在此归位**——
    /// 归位会让旧帧在切换瞬间跳回原始位置和大小。
    /// </summary>
    private void ResetDisplayFrameTransforms()
    {
        DisplayFrameZoom.ScaleX = 1;
        DisplayFrameZoom.ScaleY = 1;
        DisplayFramePan.X = 0;
        DisplayFramePan.Y = 0;
    }

    /// <summary>
    /// 把显示帧当前的内容与运动状态移交给旧帧：转场期间旧帧保持切出前的样子，
    /// 不会突然缩回初始状态或跳回画面原点。
    /// 运动状态按运动参数线性插值计算而非读属性——Storyboard 运行中依赖属性
    /// 的 getter 不保证返回动画插值，读到的可能是本地初值，直接移交就是一次跳变。
    /// </summary>
    private void HandOverToPrevious()
    {
        var (zoom, pan) = GetCurrentMotionState();

        Diagnostics.Log($"SLIDESHOW|HANDOVER|zoom={zoom:F3}|pan={pan:F0}|getter={DisplayFrameZoom.ScaleX:F3}");

        PreviousFrameElement.Source = DisplayFrameElement.Source;
        PreviousFramePan.X = pan;
        PreviousFrameZoom.ScaleX = zoom;
        PreviousFrameZoom.ScaleY = zoom;
    }

    /// <summary>
    /// 按运动形态与已进行的时长线性插值出当前画面状态（与画面动画的轨迹完全一致）。
    /// 超过动画时长（HoldEnd 保持终值）时钳制在终值。
    /// </summary>
    /// <returns>当前缩放倍率与横向偏移（逻辑像素）。</returns>
    private (double Zoom, double Pan) GetCurrentMotionState()
    {
        var total = KenBurnsDuration.TimeSpan.TotalSeconds;
        var progress = total <= 0
            ? 1
            : Math.Clamp((DateTimeOffset.UtcNow - _motionStartedUtc).TotalSeconds / total, 0, 1);

        return _currentMotion switch
        {
            KenBurnsMotion.ZoomOut => (
                KenBurnsZoomScale + (1.0 - KenBurnsZoomScale) * progress, 0),
            KenBurnsMotion.PanLeft => (
                KenBurnsPanScale, KenBurnsPanOffset + (-KenBurnsPanOffset - KenBurnsPanOffset) * progress),
            KenBurnsMotion.PanRight => (
                KenBurnsPanScale, -KenBurnsPanOffset + (KenBurnsPanOffset + KenBurnsPanOffset) * progress),
            _ => (
                1.0 + (KenBurnsZoomScale - 1.0) * progress, 0)
        };
    }

    /// <summary>画面动画的运动形态；开启动画后每张照片随机取一种。</summary>
    private enum KenBurnsMotion
    {
        /// <summary>缓慢放大。</summary>
        ZoomIn,

        /// <summary>缓慢缩小。</summary>
        ZoomOut,

        /// <summary>缓慢左移。</summary>
        PanLeft,

        /// <summary>缓慢右移。</summary>
        PanRight
    }

    /// <summary>
    /// 画面动画：从呈现时设置的初值匀速推进到终值（一律不写死起点，起止两端连续），
    /// 时长与放映间隔解耦；作用于合成帧——它始终铺满舞台且倍率恒 ≥ 1，
    /// 运动全程边缘都在视口之外。视频条目与关闭动画时不播。
    /// </summary>
    private void StartKenBurnsAnimationIfNeeded()
    {
        // HoldEnd 会把缩放与平移钉在上一轮终值，先 Stop；初值由呈现流程按运动形态设置。
        _kenBurnsStoryboard?.Stop();
        _kenBurnsStoryboard = null;

        if (ViewModel.IsCurrentVideo || !ViewModel.IsAnimationEnabled)
        {
            return;
        }

        var storyboard = new Storyboard { Duration = KenBurnsDuration };

        switch (_currentMotion)
        {
            case KenBurnsMotion.ZoomOut:
                AddZoomTracks(storyboard, null, 1.0);
                break;

            case KenBurnsMotion.PanLeft:
                AddPanTrack(storyboard, null, -KenBurnsPanOffset);
                break;

            case KenBurnsMotion.PanRight:
                AddPanTrack(storyboard, null, KenBurnsPanOffset);
                break;

            default:
                AddZoomTracks(storyboard, null, KenBurnsZoomScale);
                break;
        }

        _kenBurnsStoryboard = storyboard;
        storyboard.Begin();

        Diagnostics.Log($"SLIDESHOW|MOTION|{_currentMotion}");
    }

    /// <summary>按运动形态设置显示帧的初始变换：淡入期间画面即处于运动起点上。</summary>
    private void ApplyMotionInitialState()
    {
        _motionStartedUtc = DateTimeOffset.UtcNow;

        switch (_currentMotion)
        {
            case KenBurnsMotion.ZoomOut:
                DisplayFrameZoom.ScaleX = KenBurnsZoomScale;
                DisplayFrameZoom.ScaleY = KenBurnsZoomScale;
                break;

            case KenBurnsMotion.PanLeft:
            case KenBurnsMotion.PanRight:
                DisplayFrameZoom.ScaleX = KenBurnsPanScale;
                DisplayFrameZoom.ScaleY = KenBurnsPanScale;
                DisplayFramePan.X = _currentMotion == KenBurnsMotion.PanLeft
                    ? KenBurnsPanOffset
                    : -KenBurnsPanOffset;
                break;
        }
    }

    /// <summary>为画面动画追加匀速缩放轨道；from 为 null 时从当前值开始（不写死起点）。</summary>
    /// <param name="storyboard">画面动画。</param>
    /// <param name="from">起始缩放倍率；null 表示从当前值开始。</param>
    /// <param name="to">终止缩放倍率。</param>
    private void AddZoomTracks(Storyboard storyboard, double? from, double to)
    {
        storyboard.Children.Add(TransitionAnimationFactory.CreateLinearDoubleAnimation(DisplayFrameZoom, "ScaleX", from, to, KenBurnsDuration));
        storyboard.Children.Add(TransitionAnimationFactory.CreateLinearDoubleAnimation(DisplayFrameZoom, "ScaleY", from, to, KenBurnsDuration));
    }

    /// <summary>为画面动画追加匀速水平平移轨道；from 为 null 时从当前值开始。</summary>
    /// <param name="storyboard">画面动画。</param>
    /// <param name="from">起始横向偏移（逻辑像素）；null 表示从当前值开始。</param>
    /// <param name="to">终止横向偏移（逻辑像素）。</param>
    private void AddPanTrack(Storyboard storyboard, double? from, double to) =>
        storyboard.Children.Add(TransitionAnimationFactory.CreateLinearDoubleAnimation(DisplayFramePan, "X", from, to, KenBurnsDuration));

    /// <summary>画面平移的单侧幅度（逻辑像素）：取视口宽度的固定比例。</summary>
    private double KenBurnsPanOffset => FrameHost.ActualWidth * KenBurnsPanRatio;

    /// <summary>视图模型属性变化：同步视频源与放映播放态。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.CurrentPlaybackItem):
                ApplyPlaybackSource();
                break;

            case nameof(ViewModel.IsPlaying):
                // 暂停/继续：背景音乐跟随放映播放态（视频静音策略不受影响）。
                ComputeAudioState();
                SyncProgressPlayState();
                break;
        }
    }

    /// <summary>
    /// 呈现当前条目：渲染合成帧（虚化背景 + 照片）、移交旧帧并按切换方式播转场。
    /// 首帧与后续条目统一走本入口——画面合成本身是异步的，首帧同样需要它。
    /// </summary>
    /// <param name="isEntry">是否为本轮放映的首帧（走入场淡入而非切换转场）。</param>
    private async Task PresentFrameAsync(bool isEntry)
    {
        var sequence = ++_presentSequence;
        var newIsVideo = ViewModel.IsCurrentVideo;

        // 先停上一轮转场：HoldEnd 钉住的值只有 Stop 能解除。
        _transitionStoryboard?.Stop();
        _transitionStoryboard = null;

        // 旧帧沿用当前画面的内容与运动终态——必须在 Stop 画面动画之前读取：
        // Stop 会解除 HoldEnd，缩放/平移会回落为本地初值，移交就拿到错误的回落值了。
        HandOverToPrevious();

        _kenBurnsStoryboard?.Stop();
        _kenBurnsStoryboard = null;

        // 渲染新帧：视频条目无合成画面，显示层交给 MediaPlayerElement。
        ImageSource? frame = null;

        if (!newIsVideo && ViewModel.CurrentItem is { Path.Length: > 0 } item)
        {
            frame = await _frameRenderer.CreateFrameAsync(
                item.Path,
                FrameHost.ActualWidth,
                FrameHost.ActualHeight,
                ViewModel.RotationDegrees,
                ViewModel.IsBlurBackdrop);
        }

        if (sequence != _presentSequence)
        {
            return;   // 合成期间已切到更新的条目，丢弃本轮结果。
        }

        Diagnostics.Log(
            $"SLIDESHOW|FRAME|seq={sequence}|video={newIsVideo}|frame={(frame is null ? "none" : "ok")}"
            + $"|host={FrameHost.ActualWidth:F0}x{FrameHost.ActualHeight:F0}");

        // 合成期间画面保持上一帧不动，此刻才切换内容并复位起始值。
        DisplayFrameElement.Source = frame;
        DisplayFrameElement.Opacity = isEntry ? 1 : 0;
        PreviousFrameElement.Opacity = 1;
        VideoFrameElement.Opacity = 1;
        VideoFrameTransform.X = 0;
        ResetDisplayFrameTransforms();

        // 随机决定本轮 Ken Burns 运动并按其设置初始变换：淡入期间画面就处于运动起点上，
        // 画面动画启动时从当前值继续，起止两端都不会跳变。
        _currentMotion = (KenBurnsMotion)Random.Shared.Next(Enum.GetValues<KenBurnsMotion>().Length);
        ApplyMotionInitialState();

        if (isEntry)
        {
            _hasEntryAnimationPlayed = true;
        }

        // 首帧合成完成，撤掉装载覆盖层（合成期间它遮挡尚未就绪的画面）。
        LoadingLayer.Visibility = Visibility.Collapsed;
        UpdateFailedHint();

        var duration = ViewModel.Transition switch
        {
            SlideShowTransitionMode.Slide => SlideTransitionDuration,
            SlideShowTransitionMode.Dissolve => DissolveTransitionDuration,
            _ => FadeTransitionDuration
        };

        Storyboard storyboard;
        var hasPreviousFrame = PreviousFrameElement.Source is not null;
        FrameworkElement newTarget = newIsVideo ? VideoFrameElement : DisplayFrameElement;

        if (!hasPreviousFrame)
        {
            // 无旧帧可对照（首次装载 / 旧视频退场）：新帧单帧淡入。
            storyboard = new Storyboard { Duration = duration };
            storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(newTarget, "Opacity", 0, 1, duration));
        }
        else
        {
            storyboard = ViewModel.Transition switch
            {
                SlideShowTransitionMode.Slide => TransitionAnimationFactory.CreateSlideStoryboard(
                    PreviousFrameElement,
                    PreviousFramePan,
                    newTarget,
                    newIsVideo ? VideoFrameTransform : DisplayFramePan,
                    FrameHost.ActualWidth * TransitionAnimationFactory.SlideOffsetRatio,
                    duration),
                SlideShowTransitionMode.Dissolve => TransitionAnimationFactory.CreateDissolveStoryboard(
                    PreviousFrameElement,
                    newTarget,
                    null,
                    PreviousFrameZoom,
                    duration),
                _ => TransitionAnimationFactory.CreateFadeStoryboard(PreviousFrameElement, newTarget, duration)
            };
        }

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;

            // 旧帧已在转场中淡出完毕，直接清空即可，不会再有残留或拖尾。
            ViewModel.CompleteTransition();

            // 画面动画在转场结束后才开始：转场期间新帧静止淡入，
            // 若同时启动会与滑动转场的平移复位相互拉扯，表现为切换后的抖动。
            StartKenBurnsAnimationIfNeeded();
        }

        storyboard.Completed += OnCompleted;

        _transitionStoryboard = storyboard;
        storyboard.Begin();

        // 底部进度对应停留时间，从转场开始起算。
        RestartProgress();
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

        // 先置透明防闪帧：淡入动画要等呈现流程接管透明度。
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

    /// <summary>失败提示仅在首帧已呈现过、且当前无任何可显示画面时出现（加载期间静默）。</summary>
    private void UpdateFailedHint() =>
        FailedHint.Visibility = !_hasEntryAnimationPlayed || ViewModel.DisplayPath is not null
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

    /// <summary>点击画面任意处切换工具栏显隐；关闭只走 Esc 与工具栏关闭按钮，防误触。</summary>
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

    /// <summary>新帧可显示时由视图模型发起转场请求（首帧同样经此呈现）。</summary>
    private void OnTransitionRequested(object? sender, EventArgs e)
    {
        // 请求发自解码 await 之后的线程，画面合成与呈现必须切回 UI 线程。
        DispatcherQueue.TryEnqueue(() => _ = PresentFrameAsync(false));
    }

    /// <summary>按当前条目重启底部进度：图片走停留时间，视频走播放进度。</summary>
    private void RestartProgress()
    {
        StopProgress();

        if (ViewModel.IsCurrentVideo)
        {
            StartVideoProgress();
            return;
        }

        StartImageProgress();
    }

    /// <summary>图片条目：进度按放映间隔从 0 走到 1；未处于放映中则起手即暂停。</summary>
    private void StartImageProgress()
    {
        var duration = new Duration(TimeSpan.FromSeconds(Math.Max(1, ViewModel.IntervalSeconds)));
        var storyboard = new Storyboard { Duration = duration };

        // ProgressBar.Value 影响布局，属依赖动画，不显式启用则框架直接跳过（值纹丝不动且无报错）。
        storyboard.Children.Add(TransitionAnimationFactory.CreateLinearDoubleAnimation(
            ProgressFill, "Value", 0, 1, duration, enableDependentAnimation: true));

        _progressStoryboard = storyboard;
        storyboard.Begin();

        if (!ViewModel.IsPlaying)
        {
            storyboard.Pause();
        }
    }

    /// <summary>视频条目：按轮询节拍读取播放位置——MediaPlayer 无进度变化事件可订阅。</summary>
    private void StartVideoProgress()
    {
        _progressTimer.Interval = TimeSpan.FromMilliseconds(ProgressPollMilliseconds);
        _progressTimer.Start();
    }

    /// <summary>轮询到点：按播放位置在截取区间（或整段）中的占比推进进度条。</summary>
    private void OnProgressTimerTick(DispatcherQueueTimer sender, object args)
    {
        var session = _player?.PlaybackSession;

        if (session is null)
        {
            return;
        }

        var start = ViewModel.CurrentVideoSegmentStart ?? TimeSpan.Zero;
        var end = ViewModel.CurrentVideoSegmentEnd ?? session.NaturalDuration;
        var span = end - start;

        if (span <= TimeSpan.Zero)
        {
            return;
        }

        AnimateProgressTo(Math.Clamp((session.Position - start) / span, 0, 1));
    }

    /// <summary>把进度条补间到目标值：不设 from，从当前值起走，节拍之间连续无跳动。</summary>
    /// <param name="value">目标进度（0–1）。</param>
    private void AnimateProgressTo(double value)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(ProgressPollMilliseconds));
        var storyboard = new Storyboard { Duration = duration };

        storyboard.Children.Add(TransitionAnimationFactory.CreateLinearDoubleAnimation(
            ProgressFill, "Value", null, value, duration, enableDependentAnimation: true));
        storyboard.Begin();
    }

    /// <summary>停止进度并复位：切换条目、关闭页面与暂停放映时调用。</summary>
    private void StopProgress()
    {
        _progressStoryboard?.Stop();
        _progressStoryboard = null;
        _progressTimer.Stop();
        ProgressFill.Value = 0;
    }

    /// <summary>放映播放态变化：图片进度随之暂停 / 继续（视频进度由播放器自身状态决定）。</summary>
    private void SyncProgressPlayState()
    {
        if (_progressStoryboard is null)
        {
            return;
        }

        if (ViewModel.IsPlaying)
        {
            _progressStoryboard.Resume();
        }
        else
        {
            _progressStoryboard.Pause();
        }
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
