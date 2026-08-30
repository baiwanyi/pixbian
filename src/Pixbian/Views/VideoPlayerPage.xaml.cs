/**
 * 视频播放器页代码后置（M4）。
 * 职责：创建 MediaPlayer 并交给 ViewModel，把界面控件的变化转换为 ViewModel 命令，
 *      并在离开页面时释放播放器资源。
 * 复用约定：MediaPlayer 由本页面创建后注入 ViewModel，ViewModel 不负责创建 UI 相关对象；
 *          进度与音量用 Slider 的秒数/浮点数承载，避免 XAML 直接绑定 TimeSpan。
 * 关键约束：必须在 Unloaded 时释放 MediaPlayer，否则解码器不会回收，反复进出会导致内存持续增长；
 *          Slider 的 ValueChanged 会在代码赋值时也触发，必须与用户拖动区分，
 *          否则会出现"拖动 → 触发 Seek → 更新 Position → 再次触发 ValueChanged"的回环；
 *          全屏切换须通过 AppWindow，且退出时应恢复原窗口状态。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Models;
using Pixbian.ViewModels;
using Windows.Media.Playback;

namespace Pixbian.Views;

/// <summary>视频播放器页。</summary>
public sealed partial class VideoPlayerPage : Page, INotifyPropertyChanged, IDisposable
{
    private static readonly double[] Rates = [0.25, 0.5, 1.0, 1.5, 2.0];

    private MediaPlayer? _player;
    private bool _isApplyingProgress;
    private int _rateIndex = 2;

    /// <summary>初始化播放器页。</summary>
    /// <param name="viewModel">播放器视图模型，由依赖注入提供。</param>
    public VideoPlayerPage(VideoPlayerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        InitializeComponent();

        // MediaPlayer 不在构造期创建：本页作为单例在应用启动时即被解析，
        // 此时 XAML 资源与页面尚未加载完成，过早创建 WinRT 播放器会导致 STATUS_STOWED_EXCEPTION(0xc000027b) 崩溃。
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

    /// <summary>载入指定媒体并开始播放。</summary>
    /// <param name="item">媒体条目。</param>
    public async Task OpenAsync(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        await ViewModel.LoadAsync(item, EnsurePlayer());
        ViewModel.SetPlaybackRate(Rates[_rateIndex]);
        ViewModel.Play();
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
        // 音量滑块仅由用户驱动，ViewModel 不会反向改写其值，故无需像进度条那样加回环防护。
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
