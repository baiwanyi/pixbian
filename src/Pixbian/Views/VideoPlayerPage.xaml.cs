/**
 * 视频播放器页代码后置（M4）。
 * 职责：创建 MediaPlayer 并注入 ViewModel，把界面控件的变化转换为 ViewModel 命令，
 *      维护画面旋转（RotateTransform + 交换容器尺寸）、返回按钮悬停态与键盘交互，
 *      并在离开页面时释放播放器资源。
 * 复用约定：进度与音量用 Slider 的秒数/浮点数承载，避免 XAML 直接绑定 TimeSpan；
 *          播放态本页铺满整个窗口（含系统标题栏那 48px），左上角常驻返回按钮落在非客户区，
 *          须经主窗口登记 Passthrough 才收得到指针。
 * 关键约束：Unloaded 时必须释放 MediaPlayer，否则解码器不回收，反复进出内存持续增长；
 *          Slider 的 ValueChanged 在代码赋值时也触发，用 _isApplyingProgress 阻断回环；
 *          键盘经 PreviewKeyDown 拦截（空格在聚焦按钮上会于 KeyUp 触发 Click，双重切换），
 *          舞台 IsTabStop=True 保证单击画面后焦点留在页内、快捷键不失效；
 *          全屏状态由 MainWindow 切换后回推（OnWindowFullScreenChanged），页面不自持真相；
 *          长按快进经临时倍速实现，KeyUp 必须恢复用户倍率，卸载时须停掉全部计时器。
 */

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.Core.Models;
using Pixbian.ViewModels;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;

namespace Pixbian.Views;

/// <summary>视频播放器页。</summary>
public sealed partial class VideoPlayerPage : Page, INotifyPropertyChanged, IDisposable
{
    /// <summary>单击 ←/→ 的跳转步进。</summary>
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    /// <summary>全屏态底栏自动隐藏的静置时长。</summary>
    private static readonly TimeSpan BottomBarAutoHideDelay = TimeSpan.FromSeconds(3);

    /// <summary>按住 ←/→ 判定进入长按模式的阈值。</summary>
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(300);

    /// <summary>长按快退的连续跳转节拍。</summary>
    private static readonly TimeSpan RewindTickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>长按方向；快进经临时倍速实现，快退因倍率不支持负值改用节拍跳转。</summary>
    private enum HoldDirection
    {
        None,
        Left,
        Right
    }

    private MediaPlayer? _player;
    private bool _isApplyingProgress;
    private bool _isStageLoaded;
    private int _rotationDegrees;
    private HoldDirection _holdDirection = HoldDirection.None;
    private bool _isHoldActive;

    /// <summary>长按判定计时器：300ms 内松开按单击跳转处理，超时进入快进/快退。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _holdTimer;

    /// <summary>长按快退的节拍计时器。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _rewindTimer;

    /// <summary>全屏态下底栏自动隐藏计时器；窗口态底栏常驻不使用。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _bottomBarTimer;

    /// <summary>底栏可见性：窗口态恒为 true；全屏态默认隐藏、点击画面或移动指针唤出后自动收起。</summary>
    public bool IsBottomBarVisible { get; private set; } = true;

    /// <summary>初始化播放器页。</summary>
    /// <param name="viewModel">播放器视图模型，由依赖注入提供。</param>
    public VideoPlayerPage(VideoPlayerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        InitializeComponent();

        // 不用渲染帧事件做任何周期工作——本项目已定论其会压死合成线程。
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _holdTimer = queue.CreateTimer();
        _holdTimer.Interval = HoldThreshold;
        _holdTimer.Tick += OnHoldTimerTick;

        _rewindTimer = queue.CreateTimer();
        _rewindTimer.Interval = RewindTickInterval;
        _rewindTimer.IsRepeating = true;
        _rewindTimer.Tick += (_, _) => ViewModel.Seek(ViewModel.Position - SeekStep);

        _bottomBarTimer = queue.CreateTimer();
        _bottomBarTimer.Interval = BottomBarAutoHideDelay;
        _bottomBarTimer.Tick += (_, _) => SetBottomBarVisible(false);

        // MediaPlayer 不在构造期创建：本页作为单例在应用启动时即被解析，
        // 过早创建 WinRT 播放器会导致 STATUS_STOWED_EXCEPTION(0xc000027b) 崩溃。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>播放器视图模型。</summary>
    public VideoPlayerViewModel ViewModel { get; }

    /// <summary>播放/暂停按钮图标。</summary>
    public string PlayPauseGlyph => ViewModel.IsPlaying ? "\uE769" : "\uE768";

    /// <summary>音量按钮图标。</summary>
    public string VolumeGlyph => ViewModel is { IsMuted: true } or { Volume: <= 0 }
        ? "\uE74F"
        : "\uE767";

    /// <summary>是否存在有效时长，决定进度条是否可用。</summary>
    public bool HasDuration => ViewModel.Duration > TimeSpan.Zero;

    /// <summary>时长的总秒数，供 Slider 使用。</summary>
    public double DurationSeconds => ViewModel.Duration.TotalSeconds;

    /// <summary>当前进度的秒数，供 Slider 使用。</summary>
    public double PositionSeconds => ViewModel.Position.TotalSeconds;

    /// <summary>当前窗口是否处于全屏态：全屏时隐藏底栏、全屏按钮切换为「返回窗口」。</summary>
    public bool IsWindowFullScreen { get; private set; }

    /// <summary>全屏按钮图标：窗口态进入全屏、全屏态返回窗口。</summary>
    public string FullScreenGlyph => IsWindowFullScreen ? "\uE73F" : "\uE740";

    /// <summary>全屏按钮的提示文本（tooltip 由代码设置）。</summary>
    public string FullScreenText => IsWindowFullScreen ? "返回窗口（ESC / F11）" : "全屏播放（F11）";

    /// <summary>主窗口全屏切换后回推状态，驱动底栏显隐与全屏按钮外观。</summary>
    /// <param name="isFullScreen">切换后的全屏状态。</param>
    public void OnWindowFullScreenChanged(bool isFullScreen)
    {
        if (IsWindowFullScreen == isFullScreen)
        {
            return;
        }

        IsWindowFullScreen = isFullScreen;
        OnPropertyChanged();
        OnPropertyChanged(nameof(FullScreenGlyph));
        ToolTipService.SetToolTip(FullScreenButton, FullScreenText);

        // 全屏：底栏搬入舞台行底部、浮于画面上（占位行高归零），点击画面/移动指针唤出；
        // 回窗口：恢复独立布局行常驻。
        // 同时把焦点拉回舞台——原焦点若在底栏按钮上，底栏收起会让焦点悬空，Esc/F11 即失效。
        if (isFullScreen)
        {
            Grid.SetRow(BottomBar, 0);
            BottomBar.VerticalAlignment = VerticalAlignment.Bottom;
            BottomBarRow.Height = new GridLength(0);
            BottomBarBackdrop.Opacity = 0.3;
            SetBottomBarVisible(false);
        }
        else
        {
            _bottomBarTimer.Stop();
            Grid.SetRow(BottomBar, 1);
            BottomBar.VerticalAlignment = VerticalAlignment.Stretch;
            BottomBarRow.Height = GridLength.Auto;
            BottomBarBackdrop.Opacity = 1.0;
            SetBottomBarVisible(true);
        }

        StageRoot.Focus(FocusState.Programmatic);
    }

    /// <summary>设置底栏可见性并通知绑定。</summary>
    private void SetBottomBarVisible(bool visible)
    {
        if (IsBottomBarVisible == visible)
        {
            return;
        }

        IsBottomBarVisible = visible;
        OnPropertyChanged(nameof(IsBottomBarVisible));
    }

    /// <summary>全屏态唤出底栏并重启自动隐藏计时；窗口态底栏常驻，无需处理。</summary>
    private void ShowBottomBar()
    {
        if (!IsWindowFullScreen)
        {
            return;
        }

        _bottomBarTimer.Stop();
        SetBottomBarVisible(true);
        _bottomBarTimer.Start();
    }

    /// <summary>单击画面：全屏态唤出底栏；同时保证焦点落回舞台（快捷键不失效）。</summary>
    private void OnStageTapped(object sender, TappedRoutedEventArgs e)
    {
        StageRoot.Focus(FocusState.Programmatic);
        ShowBottomBar();
    }

    /// <summary>指针在舞台内移动：全屏态唤出底栏并重置自动隐藏计时。</summary>
    private void OnStagePointerMoved(object sender, PointerRoutedEventArgs e) => ShowBottomBar();

    /// <summary>播放模式图标（四态）。</summary>
    public string LoopModeGlyph => ViewModel.LoopMode switch
    {
        PlaybackLoopMode.Shuffle => "\uE8B1",
        PlaybackLoopMode.Sequential => "\uEA37",
        PlaybackLoopMode.RepeatOne => "\uE8ED",
        _ => "\uE8EE"
    };

    /// <summary>播放模式的提示文本（tooltip 由代码设置）。</summary>
    public string LoopModeText => ViewModel.LoopMode switch
    {
        PlaybackLoopMode.Shuffle => "随机播放（CTRL + H）",
        PlaybackLoopMode.Sequential => "列表播放（CTRL + H）",
        PlaybackLoopMode.RepeatOne => "单曲循环（CTRL + H）",
        _ => "列表循环（CTRL + H）"
    };

    /// <summary>位于系统标题栏区域内的交互控件；主窗口据此登记指针 Passthrough 矩形。</summary>
    /// <remarks>
    /// 播放态本页铺满整个窗口，左上角常驻返回按钮落在非客户区内，默认收不到指针输入，
    /// 必须由主窗口把它的矩形登记为 Passthrough（放行后点击才归 XAML）。
    /// 只登记按钮而非整条区域：整条放行会失去拖拽窗口的能力。
    /// </remarks>
    public IReadOnlyList<FrameworkElement> TitleBarInteractiveElements => [BackButton];

    /// <summary>以播放队列打开：队列经 ViewModel 维护，上/下一条与自动连播按播放模式工作。</summary>
    /// <param name="items">视频条目队列（调用方过滤掉非视频条目）。</param>
    /// <param name="startIndex">起始索引。</param>
    public async Task OpenAsync(IReadOnlyList<MediaItem> items, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        ViewModel.SetQueue(items, startIndex);
        await ViewModel.LoadAsync(items[ViewModel.QueueIndex], EnsurePlayer());

        // 新建的 MediaPlayer 不继承上次会话的倍率，重申一次当前设定值。
        ViewModel.SetPlaybackRate(ViewModel.PlaybackRate);

        if (!ViewModel.HasError)
        {
            ViewModel.Play();
        }

        // 进入播放必须保证快捷键立即生效：页面是单例反复进出，
        // 焦点可能悬空或落在页外，落焦到舞台（IsTabStop=True，可成功获焦）兜底。
        if (_isStageLoaded)
        {
            StageRoot.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>以单条队列打开指定媒体并开始播放。</summary>
    /// <param name="item">媒体条目。</param>
    public async Task OpenAsync(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        await OpenAsync([item], 0);
    }

    /// <summary>惰性创建播放器并绑定到界面元素。</summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player is null)
        {
            _player = new MediaPlayer();
            PlayerElement.SetMediaPlayer(_player);
        }

        return _player;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 舞台 IsTabStop=True 才能作为焦点目标：Page 自身不可聚焦，Focus 会静默失败，
        // 导致「进入播放后快捷键不响应」。
        _isStageLoaded = true;
        StageRoot.Focus(FocusState.Programmatic);

        // 初始显示与旋转状态（舞台尺寸此时已可用）。
        ToolTipService.SetToolTip(LoopModeButton, LoopModeText);
        UpdatePositionText();
        UpdateLoopModeIcon();
        UpdateVolumeIcons();
        ApplyRotation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        // 必须释放播放器，否则解码器不会回收。
        ViewModel.Dispose();

        _player?.Dispose();
        _player = null;
        PlayerElement.SetMediaPlayer(null);

        // 离开页面须停表并复位：页面为单例，残留的计时与状态会带到下一次打开。
        _holdTimer.Stop();
        _rewindTimer.Stop();
        _bottomBarTimer.Stop();
        _holdDirection = HoldDirection.None;
        _isHoldActive = false;
        _isStageLoaded = false;
        IsBottomBarVisible = true;
    }

    // —— 常驻返回按钮悬停态 ——

    /// <summary>指针进入返回按钮：图标提亮到 80% 白。</summary>
    private void OnBackButtonPointerEntered(object sender, PointerRoutedEventArgs e) => BackButtonIcon.Opacity = 0.8;

    /// <summary>指针离开返回按钮：图标回落到 60% 白。</summary>
    private void OnBackButtonPointerExited(object sender, PointerRoutedEventArgs e) => BackButtonIcon.Opacity = 0.6;

    // —— 画面旋转 ——

    /// <summary>旋转画面：每次顺时针转 90°，四态循环。</summary>
    private void OnRotateClick(object sender, RoutedEventArgs e)
    {
        _rotationDegrees = (_rotationDegrees + 90) % 360;
        ApplyRotation();
    }

    /// <summary>舞台尺寸变化（进出全屏、拉窗口）后按当前角度重排容器。</summary>
    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyRotation();

    /// <summary>切换视频（含自动连播）后重置旋转为 0°。</summary>
    private void ResetRotation()
    {
        if (_rotationDegrees == 0)
        {
            return;
        }

        _rotationDegrees = 0;
        ApplyRotation();
    }

    /// <summary>按角度施加旋转变换：90°/270° 交换容器宽高并居中，0°/180° 拉伸铺满舞台。</summary>
    private void ApplyRotation()
    {
        VideoRotation.Angle = _rotationDegrees;

        if (_rotationDegrees is 90 or 270)
        {
            // 交换宽高后旋转的视觉区域恰好等于舞台（元素内视频仍 Uniform 适配，行为与 0° 一致）。
            VideoContainer.HorizontalAlignment = HorizontalAlignment.Center;
            VideoContainer.VerticalAlignment = VerticalAlignment.Center;
            VideoContainer.Width = StageRoot.ActualHeight;
            VideoContainer.Height = StageRoot.ActualWidth;
        }
        else
        {
            VideoContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            VideoContainer.VerticalAlignment = VerticalAlignment.Stretch;
            VideoContainer.Width = double.NaN;
            VideoContainer.Height = double.NaN;
        }

        ToolTipService.SetToolTip(RotateButton, $"旋转 {_rotationDegrees}°（CTRL + R）");
    }

    // —— 键盘交互 ——

    /// <summary>键盘拦截：统一在 PreviewKeyDown 处理并置 Handled，阻断按钮的默认空格激活。</summary>
    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 依赖 KeyUp 结束长按，系统自动重复的 KeyDown 一律忽略。
        if (e.KeyStatus.WasKeyDown)
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePlayPause();
                e.Handled = true;
                break;

            case VirtualKey.F11:
                e.Handled = true;
                App.Services.GetRequiredService<MainWindow>().ToggleFullScreen();
                break;

            case VirtualKey.Left:
            case VirtualKey.Right:
                e.Handled = true;

                // Ctrl+←/→ 为切换上/下一项（切条目不做长按快进/快退）。
                if (IsControlDown())
                {
                    if (e.Key == VirtualKey.Left)
                    {
                        _ = ViewModel.PlayPreviousAsync();
                    }
                    else
                    {
                        _ = ViewModel.PlayNextAsync();
                    }

                    break;
                }

                _holdTimer.Stop();
                _holdDirection = e.Key == VirtualKey.Left ? HoldDirection.Left : HoldDirection.Right;
                _holdTimer.Start();
                break;

            case VirtualKey.R when IsControlDown():
                e.Handled = true;
                OnRotateClick(sender, e);
                break;

            case VirtualKey.H when IsControlDown():
                e.Handled = true;
                ViewModel.ToggleLoopModeCommand.Execute(null);
                break;

            case VirtualKey.Escape:
                e.Handled = true;
                var window = App.Services.GetRequiredService<MainWindow>();
                if (window.IsFullScreen)
                {
                    window.ToggleFullScreen();
                }
                else
                {
                    window.CloseViewer();
                }

                break;
        }
    }

    /// <summary>检测 Ctrl 键是否按下（Ctrl 组合快捷键与普通按键共用同一 KeyDown 入口）。</summary>
    private static bool IsControlDown() =>
        InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>按键释放：未达长按阈值按单击跳转处理，已达则结束快进/快退并恢复用户倍率。</summary>
    private void OnPageKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        var direction = e.Key == VirtualKey.Left ? HoldDirection.Left : HoldDirection.Right;

        // 与当前按住方向不一致（快速连击另一方向）时不处理。
        if (_holdDirection != direction)
        {
            return;
        }

        _holdTimer.Stop();
        _rewindTimer.Stop();

        var wasHoldActive = _isHoldActive;
        _holdDirection = HoldDirection.None;
        _isHoldActive = false;

        if (wasHoldActive)
        {
            // 快进（右）恢复用户倍率；快退（左）的节拍计时已在上方停止。
            if (direction == HoldDirection.Right)
            {
                ViewModel.EndTemporaryRate();
            }

            return;
        }

        // 单击跳转：暂停态同样有效（只挪进度不播放）。
        var target = direction == HoldDirection.Right
            ? ViewModel.Position + SeekStep
            : ViewModel.Position - SeekStep;
        ViewModel.Seek(target);
    }

    /// <summary>长按判定超时：进入快进（临时倍速）或快退（节拍跳转）；暂停态不进入长按模式。</summary>
    private void OnHoldTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _holdTimer.Stop();

        if (_holdDirection == HoldDirection.None || !ViewModel.IsPlaying)
        {
            return;
        }

        _isHoldActive = true;

        if (_holdDirection == HoldDirection.Right)
        {
            ViewModel.BeginTemporaryRate();
        }
        else
        {
            ViewModel.Seek(ViewModel.Position - SeekStep);
            _rewindTimer.Start();
        }
    }

    // —— ViewModel 联动 ——

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoPlayerViewModel.IsPlaying):
                OnPropertyChanged(nameof(PlayPauseGlyph));
                break;

            case nameof(VideoPlayerViewModel.Position):
                ApplyProgressToSlider();
                OnPropertyChanged(nameof(PositionSeconds));
                UpdatePositionText();
                break;

            case nameof(VideoPlayerViewModel.Duration):
                OnPropertyChanged(nameof(HasDuration));
                OnPropertyChanged(nameof(DurationSeconds));
                UpdatePositionText();
                break;

            case nameof(VideoPlayerViewModel.Volume):
            case nameof(VideoPlayerViewModel.IsMuted):
                OnPropertyChanged(nameof(VolumeGlyph));
                UpdateVolumeIcons();
                break;

            case nameof(VideoPlayerViewModel.LoopMode):
                UpdateLoopModeIcon();
                ToolTipService.SetToolTip(LoopModeButton, LoopModeText);
                break;

            case nameof(VideoPlayerViewModel.CurrentItem):
                // 切换视频（含自动连播）后画面旋转复位。
                ResetRotation();
                break;
        }
    }

    /// <summary>把当前时间 / 总时长合并文本写入进度条右侧（代码驱动，不走 x:Bind 动态更新）。</summary>
    private void UpdatePositionText() => PositionTextBlock.Text = ViewModel.PositionText;

    /// <summary>按当前播放模式刷新播放模式与静音开关图标（代码驱动）。</summary>
    private void UpdateLoopModeIcon() => LoopModeIcon.Glyph = LoopModeGlyph;

    /// <summary>按音量 / 静音状态刷新音量按钮与静音开关图标（代码驱动）。</summary>
    private void UpdateVolumeIcons()
    {
        VolumeIcon.Glyph = VolumeGlyph;
        MuteToggleIcon.Glyph = VolumeGlyph;
    }

    /// <summary>把 ViewModel 的进度同步到滑块；用标志位阻断回环。</summary>
    private void ApplyProgressToSlider()
    {
        _isApplyingProgress = true;
        ProgressSlider.Value = PositionSeconds;
        _isApplyingProgress = false;
    }

    private void OnProgressValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // 代码同步造成的变更不应回写，否则会与用户拖动打架。
        if (_isApplyingProgress)
        {
            return;
        }

        ViewModel.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // 音量滑块与 ViewModel.Volume 是 OneWay 绑定，SetVolume 会回写滑块；
        // 但回写值与用户拖动的终值相同，一次往返即收敛，故无需像进度条那样加回环防护。
        ViewModel.SetVolume(e.NewValue);
    }

    private void OnTogglePlayPauseClick(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause();

    private void OnLoopModeClick(object sender, RoutedEventArgs e) => ViewModel.ToggleLoopModeCommand.Execute(null);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = ViewModel.PlayPreviousAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = ViewModel.PlayNextAsync();

    /// <summary>速度子菜单打开时按实际倍率刷新勾选态（含容差，避免浮点比较失配）。</summary>
    private void OnRateMenuFlyoutOpening(object? sender, object? e)
    {
        var speedItem = RateMenu.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault();

        if (speedItem is null)
        {
            return;
        }

        foreach (var item in speedItem.Items.OfType<RadioMenuFlyoutItem>())
        {
            var rate = Convert.ToDouble(item.Tag, CultureInfo.InvariantCulture);
            item.IsChecked = Math.Abs(ViewModel.PlaybackRate - rate) < 0.01;
        }
    }

    /// <summary>选择倍速档位；临时倍速（长按快进）不经此路径，不污染用户设定。</summary>
    private void OnRateMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item)
        {
            ViewModel.SetPlaybackRate(Convert.ToDouble(item.Tag, CultureInfo.InvariantCulture));
        }
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e)
    {
        var window = App.Services.GetRequiredService<MainWindow>();
        window.ToggleFullScreen();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<MainWindow>().CloseViewer();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
