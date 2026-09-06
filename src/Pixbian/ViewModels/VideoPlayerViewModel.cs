/**
 * 视频播放器视图模型（M4）。
 * 职责：管理 MediaPlayer 的播放状态、进度、音量与倍速，并异步加载视频元数据。
 * 复用约定：播放源优先由 FFmpegInteropX 创建（FFmpeg 解码：dav1d 软解 + D3D11 自动硬解），
 *          失败时回退 MediaSource.CreateFromStorageFile 走系统解码器；
 *          呈现仍由 MediaPlayerElement 承担，界面与交互不因解码后端变化而改变；
 *          倍速通过 PlaybackSession.PlaybackRate 设置，音量与静音直接操作 MediaPlayer。
 * 关键约束：MediaPlayer 持有非托管资源，离开页面时必须 Dispose，否则解码器不会释放；
 *          进度同步用 DispatcherQueueTimer 轮询而非依赖 PositionChanged 事件——
 *          后者在拖动进度条时会与用户输入打架，导致滑块来回跳动；
 *          倍速仅影响播放速度，不改变音频音调处理，取值范围须做钳制。
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFmpegInteropX;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Models;
using Pixbian.Media.Models;
using Pixbian.Media.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Pixbian.ViewModels;

/// <summary>视频播放器视图模型。</summary>
public sealed partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private const double MinRate = 0.25;
    private const double MaxRate = 4.0;

    private readonly IVideoMetadataReader _metadataReader;
    private readonly DispatcherQueueTimer _progressTimer;

    [ObservableProperty]
    private MediaItem? _currentItem;

    [ObservableProperty]
    private VideoMetadata? _metadata;

    [ObservableProperty]
    private MediaPlaybackItem? _playbackItem;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private double _playbackRate = 1.0;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "就绪";

    private MediaPlayer? _player;
    private bool _isSeeking;

    /// <summary>FFmpeg 解码源；播放期间必须由字段强引用，被 GC 回收会导致播放中断。</summary>
    private FFmpegMediaSource? _ffmpegSource;

    public VideoPlayerViewModel(
        IVideoMetadataReader metadataReader,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);

        _metadataReader = metadataReader;

        var queue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _progressTimer = queue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += OnProgressTick;
    }

    /// <summary>播放速率的可读文本。</summary>
    public string PlaybackRateText => $"{PlaybackRate.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)}×";

    /// <summary>音量百分比文本。</summary>
    public string VolumeText => $"{(int)Math.Round(Volume * 100)}%";

    /// <summary>当前进度的可读文本。</summary>
    public string PositionText => Metadata?.DurationText is { } d ? $"{FormatTime(Position)} / {d}" : FormatTime(Position);

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>载入视频并准备播放。</summary>
    /// <param name="item">媒体条目。</param>
    /// <param name="player">由界面提供的播放器实例。</param>
    public async Task LoadAsync(MediaItem item, MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(player);

        CurrentItem = item;
        Metadata = null;
        HasError = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;

        _player = player;
        _player.Volume = Volume;
        _player.IsMuted = IsMuted;

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);

            // 先读元数据：解码后端要按编码选择（AV1 必须强制走 FFmpeg 软解）。
            await LoadMetadataAsync();

            PlaybackItem = await CreatePlaybackItemAsync(file);
            player.Source = PlaybackItem;

            // 元数据没给出时长时，用会话解析到的自然时长兜底（需先设置 Source 才有值）。
            if (Duration <= TimeSpan.Zero && player.PlaybackSession?.NaturalDuration > TimeSpan.Zero)
            {
                Duration = player.PlaybackSession.NaturalDuration;
            }

            StatusText = "已载入";
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                      or UnauthorizedAccessException
                                      or IOException
                                      or ArgumentException
                                      or NotSupportedException
                                      or InvalidOperationException)
        {
            HasError = true;
            StatusText = "无法播放该文件，可能是编码不受支持或文件已损坏";
        }
    }

    /// <summary>创建播放项：优先 FFmpeg 解码，FFmpeg 不可用时回退系统解码器。</summary>
    /// <param name="file">媒体文件。</param>
    /// <returns>可直接赋给 MediaPlayer 的播放项。</returns>
    /// <remarks>
    /// 系统 Media Foundation 在本机（UHD 630）对 AV1 只能软解，效率远低于自带 FFmpeg 的播放器；
    /// FFmpeg 提供 dav1d 多线程软解，并对 H264 / HEVC / AV1 / VP9 等自动探测 D3D11 原生硬解，
    /// 故统一优先走 FFmpeg，呈现仍由 MediaPlayerElement 负责、界面不受影响。
    /// 【约束】FFmpegMediaSource 必须由字段强引用：被 GC 回收会直接中断播放（官方明确警告）。
    /// 回退路径保证异常文件仍能经系统解码器播放，行为与引入 FFmpeg 前一致。
    /// </remarks>
    private async Task<MediaPlaybackItem> CreatePlaybackItemAsync(Windows.Storage.StorageFile file)
    {
        // 解码相关配置挂在 Video 子配置上：MediaSourceConfig 只是 General/Video/Audio/Subtitles 的聚合容器。
        var config = new MediaSourceConfig();

        // 默认 AutomaticSystemDecoder 会优先用系统解码器：AV1 在本机（UHD 630 无硬解）
        // 下系统解码器虽能播但走自身软解，效率远低于 FFmpeg 的 dav1d 多线程软解
        // （实测 4K AV1 长期接近 100% CPU），故对 AV1 显式强制 FFmpeg 软解。
        // 其余编码保持自动：系统解码器能硬解时不改变既有行为。
        config.Video.VideoDecoderMode = IsAv1(Metadata?.VideoCodec)
            ? VideoDecoderMode.ForceFFmpegSoftwareDecoder
            : VideoDecoderMode.AutomaticSystemDecoder;

        // FFmpeg 的解码线程数默认不是按核心数放开，不显式设置就跑不满多核。
        config.Video.MaxDecoderThreads = (uint)Environment.ProcessorCount;

        try
        {
            var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

            _ffmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(stream, config);

            LogDecodeBackend(config.Video.VideoDecoderMode);

            return _ffmpegSource.CreateMediaPlaybackItem();
        }
        catch (Exception ex) when (ex is NotSupportedException
                                      or InvalidOperationException
                                      or IOException
                                      or ArgumentException
                                      or UnauthorizedAccessException)
        {
            _ffmpegSource = null;

            Pixbian.Services.Diagnostics.Log(
                $"VIDEODEC|fallback=system|codec={Metadata?.VideoCodec}|reason={ex.GetType().Name}");

            return new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file));
        }
    }

    /// <summary>记录本次实际采用的解码后端，供排查「CPU 高」时确认是否真的走了 FFmpeg。</summary>
    /// <param name="mode">实际生效的解码模式。</param>
    private void LogDecodeBackend(VideoDecoderMode mode) =>
        Pixbian.Services.Diagnostics.Log(
            $"VIDEODEC|codec={Metadata?.VideoCodec}|mode={mode}|threads={Environment.ProcessorCount}");

    /// <summary>判断是否为 AV1：MediaClip 返回的 subtype 通常是 av01，少数环境为 av1。</summary>
    private static bool IsAv1(string? codec) =>
        codec is not null
        && (codec.Contains("av01", StringComparison.Ordinal) || codec.Contains("av1", StringComparison.Ordinal));

    /// <summary>开始播放。</summary>
    [RelayCommand]
    public void Play()
    {
        if (_player is null || PlaybackItem is null)
        {
            return;
        }

        _player.Play();
        IsPlaying = true;
        _progressTimer.Start();
    }

    /// <summary>暂停播放。</summary>
    [RelayCommand]
    public void Pause()
    {
        if (_player is null)
        {
            return;
        }

        _player.Pause();
        IsPlaying = false;
        _progressTimer.Stop();
    }

    /// <summary>切换播放与暂停。</summary>
    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>跳转到指定位置。</summary>
    /// <param name="position">目标位置。</param>
    [RelayCommand]
    public void Seek(TimeSpan position)
    {
        if (_player?.PlaybackSession is null || Duration <= TimeSpan.Zero)
        {
            return;
        }

        var clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > Duration ? Duration : position;

        _isSeeking = true;
        _player.PlaybackSession.Position = clamped;
        Position = clamped;
        _isSeeking = false;
    }

    /// <summary>设置音量。</summary>
    /// <param name="value">音量，0–1。</param>
    [RelayCommand]
    public void SetVolume(double value)
    {
        Volume = Math.Clamp(value, 0, 1);
        OnPropertyChanged(nameof(VolumeText));

        if (_player is not null)
        {
            _player.Volume = Volume;
        }
    }

    /// <summary>切换静音。</summary>
    [RelayCommand]
    public void ToggleMute()
    {
        IsMuted = !IsMuted;

        if (_player is not null)
        {
            _player.IsMuted = IsMuted;
        }
    }

    /// <summary>设置播放速率。</summary>
    /// <param name="rate">速率倍数。</param>
    [RelayCommand]
    public void SetPlaybackRate(double rate)
    {
        PlaybackRate = Math.Clamp(rate, MinRate, MaxRate);
        OnPropertyChanged(nameof(PlaybackRateText));

        if (_player?.PlaybackSession is not null)
        {
            _player.PlaybackSession.PlaybackRate = PlaybackRate;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _progressTimer.Stop();

        // 解码源与播放器一并断开引用：FFmpeg 的解码上下文占用非托管内存，
        // 只置空播放器而不释放源会让解码器与文件句柄滞留到进程回收。
        (_ffmpegSource as IDisposable)?.Dispose();
        _ffmpegSource = null;

        if (_player is null)
        {
            return;
        }

        _player.Pause();
        _player.Source = null;
        _player.Dispose();
        _player = null;
    }

    private async Task LoadMetadataAsync()
    {
        if (CurrentItem is null)
        {
            return;
        }

        Metadata = await _metadataReader.ReadAsync(CurrentItem.Path);

        if (Metadata?.Duration is { } duration)
        {
            Duration = duration;
        }
        else if (_player?.PlaybackSession?.NaturalDuration is { } natural && natural > TimeSpan.Zero)
        {
            Duration = natural;
        }
    }

    private void OnProgressTick(DispatcherQueueTimer sender, object args)
    {
        var session = _player?.PlaybackSession;

        if (session is null || _isSeeking)
        {
            return;
        }

        Position = session.Position;

        if (Duration <= TimeSpan.Zero && session.NaturalDuration > TimeSpan.Zero)
        {
            Duration = session.NaturalDuration;
        }

        // 播放到结尾时同步状态，避免按钮仍显示为播放中。
        if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(250) && IsPlaying)
        {
            IsPlaying = false;
            _progressTimer.Stop();
        }
    }
}
