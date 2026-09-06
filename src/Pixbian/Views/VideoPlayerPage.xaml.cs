/**
 * 视频播放器页代码后置（M4）。
 * 职责：创建 MediaPlayer 并交给 ViewModel，把界面控件的变化转换为 ViewModel 命令，
 *      并在离开页面时释放播放器资源。
 * 复用约定：MediaPlayer 由本页面创建后注入 ViewModel，ViewModel 不负责创建 UI 相关对象；
 *          进度与音量用 Slider 的秒数/浮点数承载，避免 XAML 直接绑定 TimeSpan。
 *          控制条为覆盖层，自动隐藏一律走 DispatcherQueue 定时器——
 *          绝不订阅 CompositionTarget.Rendering 等渲染帧事件（本项目已定论：会压死合成线程）；
 *          播放态本页铺满整个窗口（含系统标题栏那 48px），顶栏落在非客户区，
 *          其交互控件须经主窗口登记 Passthrough 才收得到指针。
 * 关键约束：必须在 Unloaded 时释放 MediaPlayer，否则解码器不会回收，反复进出会导致内存持续增长；
 *          Slider 的 ValueChanged 会在代码赋值时也触发，必须与用户拖动区分，
 *          否则会出现"拖动 → 触发 Seek → 更新 Position → 再次触发 ValueChanged"的回环；
 *          全屏切换须通过 AppWindow，且退出时应恢复原窗口状态；
 *          控制条隐藏计时器必须在 Dispose 中停表，卸载后不得残留计时回调。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.Core.Models;
using Pixbian.ViewModels;
using Windows.Media.Playback;

namespace Pixbian.Views;

/// <summary>视频播放器页。</summary>
public sealed partial class VideoPlayerPage : Page, INotifyPropertyChanged, IDisposable
{
    private static readonly double[] Rates = [0.25, 0.5, 1.0, 1.5, 2.0];

    /// <summary>控制条自动隐藏的静置时长：打开视频或唤出后无操作则收起。</summary>
    private static readonly TimeSpan ChromeAutoHideDelay = TimeSpan.FromSeconds(3);

    private MediaPlayer? _player;
    private bool _isApplyingProgress;
    private int _rateIndex = 2;
    private bool _isChromeVisible;
    private bool _isChromeHovered;
    private bool _isRateDropDownOpen;

    /// <summary>控制条自动隐藏计时器；页面卸载时必须停表，避免残留计时回调。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _chromeTimer;

    /// <summary>初始化播放器页。</summary>
    /// <param name="viewModel">播放器视图模型，由依赖注入提供。</param>
    public VideoPlayerPage(VideoPlayerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        InitializeComponent();

        // 自动隐藏计时器：Tick 回调在 UI 线程执行，直接收起控制条即可。
        // 不用 CompositionTarget.Rendering 等渲染帧事件——本项目已定论其会压死合成线程。
        _chromeTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _chromeTimer.Interval = ChromeAutoHideDelay;
        _chromeTimer.Tick += (_, _) => HideChrome();

        // MediaPlayer 不在构造期创建：本页作为单例在应用启动时即被解析，
        // 此时 XAML 资源与页面尚未加载完成，过早创建 WinRT 播放器会导致 STATUS_STOWED_EXCEPTION(0xc000027b) 崩溃。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>控制条显隐变化；主窗口据此重算系统标题栏的指针放行区域（顶栏收起时要让出拖拽区）。</summary>
    public event EventHandler? ChromeVisibilityChanged;

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

    /// <summary>倍速下拉框的当前索引。</summary>
    public int RateIndex
    {
        get => _rateIndex;
        set
        {
            if (_rateIndex == value)
            {
                return;
            }

            _rateIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>位于系统标题栏区域内的交互控件；主窗口据此登记指针 Passthrough 矩形。</summary>
    /// <remarks>
    /// 播放态本页铺满整个窗口，顶栏落在非客户区内，其中控件默认收不到指针输入，
    /// 必须由主窗口把它们的矩形登记为 Passthrough（放行后点击才归 XAML）。
    /// 只登记按钮而非整条顶栏：整条放行会让顶栏空白区失去拖拽窗口的能力。
    /// </remarks>
    public IReadOnlyList<FrameworkElement> TitleBarInteractiveElements => [BackButton];

    /// <summary>顶栏与底栏是否可见；由打开视频、指针移入舞台或单击画面驱动，静置后自动收起。</summary>
    public bool IsChromeVisible
    {
        get => _isChromeVisible;
        private set
        {
            if (_isChromeVisible == value)
            {
                return;
            }

            _isChromeVisible = value;
            OnPropertyChanged();
            ChromeVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>载入指定媒体并开始播放。</summary>
    /// <param name="item">媒体条目。</param>
    public async Task OpenAsync(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        await ViewModel.LoadAsync(item, EnsurePlayer());
        ViewModel.SetPlaybackRate(Rates[_rateIndex]);
        ViewModel.Play();

        // 打开即显示控制条：否则用户看不到返回按钮与文件名。
        ShowChrome();
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

    private void OnLoaded(object sender, RoutedEventArgs e) => Focus(FocusState.Programmatic);

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        // 必须释放播放器，否则解码器不会回收。
        ViewModel.Dispose();

        _player?.Dispose();
        _player = null;
        PlayerElement.SetMediaPlayer(null);

        // 离开页面须停表并复位：页面为单例，残留的计时与可见状态会带到下一次打开。
        _chromeTimer.Stop();
        IsChromeVisible = false;
        _isChromeHovered = false;
        _isRateDropDownOpen = false;
    }

    /// <summary>显示控制条并重启自动隐藏计时。</summary>
    private void ShowChrome()
    {
        IsChromeVisible = true;
        RestartAutoHide();
    }

    /// <summary>收起控制条并停表。</summary>
    private void HideChrome()
    {
        _chromeTimer.Stop();
        IsChromeVisible = false;
    }

    /// <summary>重启自动隐藏计时；指针停在控制条上或倍速下拉展开期间不计时，避免操作中被收起。</summary>
    private void RestartAutoHide()
    {
        _chromeTimer.Stop();

        if (_isChromeHovered || _isRateDropDownOpen)
        {
            return;
        }

        _chromeTimer.Start();
    }

    /// <summary>指针在舞台内移动：唤出控制条并重置自动隐藏计时。</summary>
    private void OnStagePointerMoved(object sender, PointerRoutedEventArgs e) => ShowChrome();

    /// <summary>单击画面切换控制条显隐；点击控制条上的控件不会走到这里（命中层在其下方）。</summary>
    private void OnStageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsChromeVisible)
        {
            HideChrome();
            return;
        }

        ShowChrome();
    }

    /// <summary>指针进入控制条：暂停自动隐藏。</summary>
    private void OnChromePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isChromeHovered = true;
        _chromeTimer.Stop();
    }

    /// <summary>指针离开控制条：恢复自动隐藏计时。</summary>
    private void OnChromePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isChromeHovered = false;
        RestartAutoHide();
    }

    /// <summary>倍速下拉展开：暂停自动隐藏（下拉浮层在控制条之外，指针已不在控制条内）。</summary>
    private void OnRateDropDownOpened(object? sender, object? e)
    {
        _isRateDropDownOpen = true;
        _chromeTimer.Stop();
    }

    /// <summary>倍速下拉收起：恢复自动隐藏计时。</summary>
    private void OnRateDropDownClosed(object? sender, object? e)
    {
        _isRateDropDownOpen = false;
        RestartAutoHide();
    }

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
                break;

            case nameof(VideoPlayerViewModel.Duration):
                OnPropertyChanged(nameof(HasDuration));
                OnPropertyChanged(nameof(DurationSeconds));
                break;

            case nameof(VideoPlayerViewModel.Volume):
            case nameof(VideoPlayerViewModel.IsMuted):
                OnPropertyChanged(nameof(VolumeGlyph));
                break;
        }
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

    private void OnToggleMuteClick(object sender, RoutedEventArgs e) => ViewModel.ToggleMute();

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RateIndex < 0 || RateIndex >= Rates.Length)
        {
            return;
        }

        ViewModel.SetPlaybackRate(Rates[RateIndex]);
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
