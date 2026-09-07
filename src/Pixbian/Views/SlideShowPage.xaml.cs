/**
 * 幻灯片放映代码后置。
 * 职责：设置数据上下文与焦点、处理键盘快捷键（翻页 / 暂停 / 退出），
 *      播放首帧入场淡入与关闭淡出动画，承担底部工具栏显隐调度与转场动画播放；
 *      创建 MediaPlayer 并注入视频帧层，把播放结束 / 失败事件桥接为视图模型的推进信号。
 * 复用约定：视图模型由依赖注入提供，转场动画经 TransitionAnimationFactory 构造，
 *          页面不持有媒体数据全部经绑定获取；页面由独立放映窗口（SlideShowWindow）
 *          承载，关闭统一经 Owner.Close() 收口，Closed 摘除内容后 Unloaded 负责清理。
 * 关键约束：动画目标直接取元素对象而非 TargetName（namescope 解析失败即静默无动画），
 *          入场/关闭/转场动画每轮前必须复位起始值（FillBehavior 默认 HoldEnd 保留终值）；
 *          关闭动画实例须存字段供下次打开前 Stop 清除其钉住效果；
 *          MediaPlayer 必须懒创建（应用启动阶段构造会触发 WinRT 异常），且在 Unloaded
 *          释放，否则解码器不回收反复放映内存持续增长；
 *          转场请求发自解码 await 之后的线程，必须切回 UI 线程播放；
 *          涉及视频的转场一律淡出（视频无帧留存，不做滑动）；
 *          工具栏淡出计时在指针悬停于工具栏上时必须暂停，否则无法点击栏内按钮。
 */

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Windows.Media.Playback;
using Windows.System;
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

    private readonly DispatcherQueueTimer _toolbarHideTimer;

    private bool _isToolbarVisible;
    private bool _hasEntryAnimationPlayed;
    private bool _isClosing;

    /// <summary>关闭淡出动画实例；HoldEnd 会把根层 Opacity 钉在 0，下次打开前必须 Stop。</summary>
    private Storyboard? _closeStoryboard;

    /// <summary>视频播放器；懒创建（首个视频条目时），Unloaded 释放。</summary>
    private MediaPlayer? _player;

    /// <summary>初始化幻灯片放映页。</summary>
    /// <param name="viewModel">放映视图模型，由依赖注入提供。</param>
    public SlideShowPage(SlideShowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnKeyDown;

        // 本页与视图模型同为 DI 单例、生命周期一致，故不在 Unloaded 里退订；
        // 若日后续任一方改为瞬态，必须在此配对退订，否则页面实例会被事件长期持有。
        ViewModel.TransitionRequested += OnTransitionRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        _toolbarHideTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toolbarHideTimer.Interval = TimeSpan.FromMilliseconds(ToolbarAutoHideMilliseconds);
        _toolbarHideTimer.IsRepeating = false;
        _toolbarHideTimer.Tick += (_, _) => HideToolbar();
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
        // 离开页面必须停止放映并释放视频资源，否则后台持续触发切换、解码器不回收。
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
            _player.Dispose();
            _player = null;
        }
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
        storyboard.Begin();
    }

    /// <summary>视图模型属性变化：首帧就绪播入场动画、更新失败提示、同步视频源与静音策略。</summary>
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

            case nameof(ViewModel.IsVideoMuted):
                if (_player is not null)
                {
                    _player.IsMuted = ViewModel.IsVideoMuted;
                }

                break;
        }
    }

    /// <summary>把视图模型的播放项变化落到播放器：换源起播或清源退出视频层。</summary>
    private void ApplyPlaybackSource()
    {
        var playback = ViewModel.CurrentPlaybackItem;

        if (playback is null)
        {
            // 视频退场即黑场过渡（视频无帧可留存），清源并隐藏视频层。
            if (_player is not null)
            {
                _player.Source = null;
            }

            VideoFrameElement.Visibility = Visibility.Collapsed;
            return;
        }

        VideoFrameElement.Visibility = Visibility.Visible;

        // 先置透明防闪帧：淡入动画要到 BeginTransition（下一轮消息）才接管透明度。
        VideoFrameElement.Opacity = 0;

        var player = EnsurePlayer();
        player.Source = playback;
        player.Play();
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
        _player.IsMuted = ViewModel.IsVideoMuted;
        VideoFrameElement.SetMediaPlayer(_player);

        return _player;
    }

    /// <summary>视频播完或播放失败：桥接为视图模型的推进信号（失败按播完处理，不中断放映）。</summary>
    private void OnPlayerMediaEnded(MediaPlayer sender, object args) => ViewModel.NotifyVideoEnded();

    private void OnPlayerMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => ViewModel.NotifyVideoEnded();

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
    /// 图片 → 图片按设置走交叉淡出或滑动；涉及视频一律淡出（视频无帧留存，不做滑动）：
    /// 有旧图留存时旧图同步淡出，否则（首次装载 / 旧视频退场）仅新帧单帧淡入。
    /// </remarks>
    private void BeginTransition()
    {
        var newIsVideo = ViewModel.IsCurrentVideo;
        var hasPreviousImage = ViewModel.PreviousImage is not null;

        // 复位起始值：HoldEnd 会保留上一轮的终值，不复位则旧帧一进场就是透明的。
        PreviousFrameElement.Opacity = 1;
        DisplayFrameElement.Opacity = 1;
        PreviousFrameTransform.X = 0;
        DisplayFrameTransform.X = 0;
        VideoFrameElement.Opacity = 1;
        VideoFrameTransform.X = 0;

        Storyboard storyboard;

        if (hasPreviousImage && !newIsVideo)
        {
            storyboard = ViewModel.Transition == SlideShowTransitionMode.Fade
                ? TransitionAnimationFactory.CreateFadeStoryboard(PreviousFrameElement, DisplayFrameElement, TransitionDuration)
                : TransitionAnimationFactory.CreateSlideStoryboard(
                    PreviousFrameElement, PreviousFrameTransform, DisplayFrameElement, DisplayFrameTransform, TransitionDuration);
        }
        else
        {
            var newTarget = newIsVideo ? (DependencyObject)VideoFrameElement : DisplayFrameElement;
            storyboard = hasPreviousImage
                ? TransitionAnimationFactory.CreateFadeStoryboard(PreviousFrameElement, newTarget, TransitionDuration)
                : CreateSingleFrameFadeStoryboard(newTarget);
        }

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            ViewModel.CompleteTransition();
        }

        storyboard.Completed += OnCompleted;
        storyboard.Begin();
    }

    /// <summary>构造单帧淡入动画：无旧帧可对照（首次装载、旧视频退场）时的进场过渡。</summary>
    private static Storyboard CreateSingleFrameFadeStoryboard(DependencyObject target)
    {
        var storyboard = new Storyboard { Duration = TransitionDuration };
        storyboard.Children.Add(TransitionAnimationFactory.CreateDoubleAnimation(target, "Opacity", 0, 1, TransitionDuration));

        return storyboard;
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
