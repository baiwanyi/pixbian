/**
 * 视频播放器视图模型（M4）。
 * 职责：管理 MediaPlayer 的播放状态、进度、音量与倍速，异步加载视频元数据，
 *      并维护播放队列（上/下一条、四态播放模式与播完自动连播）。
 * 复用约定：播放源统一经 IVideoPlaybackItemFactory 创建（FFmpeg 解码：dav1d 软解 + D3D11 自动硬解，
 *          失败时回退系统解码器），与短片页共用同一份解码策略，避免两处各自漂移；
 *          呈现仍由 MediaPlayerElement 承担，界面与交互不因解码后端变化而改变；
 *          倍速通过 PlaybackSession.PlaybackRate 设置，音量与静音直接操作 MediaPlayer。
 * 关键约束：MediaPlayer 持有非托管资源，离开页面时必须 Dispose，否则解码器不会释放；
 *          进度同步用 DispatcherQueueTimer 轮询而非依赖 PositionChanged 事件——
 *          后者在拖动进度条时会与用户输入打架，导致滑块来回跳动；
 *          播完自动连播经 DispatcherQueue.TryEnqueue 投递到下一拍执行，
 *          严禁在进度 Tick 回调栈内重入装载链路；随机模式一次性打乱队列并保证当前项在头部。
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Models;
using Pixbian.Media.Models;
using Pixbian.Media.Services;
using Pixbian.Services;
using Windows.Media.Playback;

namespace Pixbian.ViewModels;

/// <summary>播放模式：随机 → 列表播放 → 单曲循环 → 列表循环，四态循环切换，默认列表播放。</summary>
public enum PlaybackLoopMode
{
    /// <summary>随机：进入时一次性打乱队列，之后按打乱序播放。</summary>
    Shuffle,

    /// <summary>列表播放（默认）：可上/下一条，播完整队列即暂停，不回绕。</summary>
    Sequential,

    /// <summary>单曲循环：播完重播当前条目。</summary>
    RepeatOne,

    /// <summary>列表循环：尾部回绕到第一条。</summary>
    RepeatList
}

/// <summary>视频播放器视图模型。</summary>
public sealed partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private const double MinRate = 0.25;
    private const double MaxRate = 4.0;

    /// <summary>临时倍速（长按快进）的速率。</summary>
    private const double TemporaryRate = 3.0;

    /// <summary>「上一条」先回到开头的位置阈值：超过该值单击上一条只回开头不切条目。</summary>
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(5);

    private readonly IVideoMetadataReader _metadataReader;
    private readonly IVideoPlaybackItemFactory _playbackItemFactory;
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

    [ObservableProperty]
    private PlaybackLoopMode _loopMode = PlaybackLoopMode.Sequential;

    private MediaPlayer? _player;
    private bool _isSeeking;

    /// <summary>播放队列及当前索引；由 SetQueue 注入，切条目时按索引装载。</summary>
    private IReadOnlyList<MediaItem> _queue = [];
    private int _queueIndex;

    /// <summary>切条目防重入标志：装载是异步的，连播与连点必须串行。</summary>
    private bool _isSwitchingItem;

    /// <summary>临时倍速状态：进入前记住用户设定倍率，退出时恢复。</summary>
    private bool _isTemporaryRate;
    private double _userRate = 1.0;

    /// <summary>当前播放项及其 FFmpeg 解码源；切换或释放时必须一并 Dispose，否则解码上下文与文件句柄不回收。</summary>
    private VideoPlaybackItem? _playback;

    public VideoPlayerViewModel(
        IVideoMetadataReader metadataReader,
        IVideoPlaybackItemFactory playbackItemFactory,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(playbackItemFactory);

        _metadataReader = metadataReader;
        _playbackItemFactory = playbackItemFactory;

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

    /// <summary>当前进度的可读文本（合并格式，显示在进度条右侧）。</summary>
    public string PositionText => Duration > TimeSpan.Zero
        ? $"{FormatTime(Position)} / {FormatTime(Duration)}"
        : FormatTime(Position);

    /// <summary>当前模式是否在队尾回绕（列表播放不回绕）。</summary>
    private bool IsWrapMode => LoopMode is PlaybackLoopMode.Shuffle
        or PlaybackLoopMode.RepeatOne
        or PlaybackLoopMode.RepeatList;

    /// <inheritdoc />
    partial void OnPositionChanged(TimeSpan value) => OnPropertyChanged(nameof(PositionText));

    /// <inheritdoc />
    partial void OnDurationChanged(TimeSpan value) => OnPropertyChanged(nameof(PositionText));

    /// <summary>当前播放的队列索引（SetQueue 后的钳制结果，供页面按索引取条目）。</summary>
    public int QueueIndex => _queueIndex;

    /// <summary>「上一条」是否可用：队列有多条即可。</summary>
    public bool CanGoPrevious => _queue.Count > 1;

    /// <summary>「下一条」是否可用：回绕类模式队列有多条即可；列表播放到队尾禁用。</summary>
    public bool CanGoNext => _queue.Count > 1
        && (LoopMode != PlaybackLoopMode.Sequential || _queueIndex < _queue.Count - 1);

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

            // 切换源之前必须释放上一个播放项：FFmpeg 的解码上下文与文件句柄不会因重写 Source 而回收。
            _playback?.Dispose();
            _playback = await _playbackItemFactory.CreateAsync(file, Metadata);

            PlaybackItem = _playback.Item;
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

        // 长按快进可能在播放器销毁时仍处于激活态，复位标志避免下次打开时直接恢复旧倍率。
        _isTemporaryRate = false;

        // 队列持有条目引用，离开页面时一并清空，防止跨会话残留旧集合。
        _queue = [];
        _queueIndex = 0;
        LoopMode = PlaybackLoopMode.Sequential;

        // 解码源与播放器一并断开引用：FFmpeg 的解码上下文占用非托管内存，
        // 只置空播放器而不释放源会让解码器与文件句柄滞留到进程回收。
        _playback?.Dispose();
        _playback = null;

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

        // 播放到结尾时同步状态并按循环模式决定连播，避免按钮仍显示为播放中。
        if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(250) && IsPlaying)
        {
            IsPlaying = false;
            _progressTimer.Stop();
            HandlePlaybackEnded();
        }
    }

    /// <summary>播放结束后的分流：按循环模式决定重播、连播或停止。</summary>
    private void HandlePlaybackEnded()
    {
        switch (LoopMode)
        {
            case PlaybackLoopMode.RepeatOne:
                // 重播当前条目：Seek 归零后继续播放，无需重新装载解码源。
                DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                {
                    Seek(TimeSpan.Zero);
                    Play();
                });
                break;

            case PlaybackLoopMode.RepeatList:
            case PlaybackLoopMode.Shuffle:
                // 连播：装载是异步重活，必须投递到下一拍，严禁在 Tick 回调栈内重入装载链路。
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () => await PlayNextCoreAsync(wrap: true));
                break;

            default:
                // 列表播放：保持停止。
                break;
        }
    }

    /// <summary>注入播放队列与起始索引；页面每次打开视频都会重建队列。</summary>
    /// <param name="items">视频条目队列（调用方过滤掉非视频）。</param>
    /// <param name="startIndex">起始索引（越界时钳制到有效范围）。</param>
    public void SetQueue(IReadOnlyList<MediaItem> items, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            throw new ArgumentException("播放队列不能为空", nameof(items));
        }

        _queue = items.ToArray();
        _queueIndex = Math.Clamp(startIndex, 0, items.Count - 1);
        _isTemporaryRate = false;

        NotifyNavigationStateChanged();
    }

    /// <summary>切换播放模式：随机 → 列表播放 → 单曲循环 → 列表循环；进入随机时立即打乱队列。</summary>
    [RelayCommand]
    public void ToggleLoopMode()
    {
        LoopMode = (PlaybackLoopMode)(((int)LoopMode + 1) % 4);

        if (LoopMode == PlaybackLoopMode.Shuffle)
        {
            ShuffleQueue();
        }

        NotifyNavigationStateChanged();
    }

    /// <summary>跳到下一条：回绕类模式尾部回到队首，顺序播放停在队尾（按钮此时禁用）。</summary>
    [RelayCommand]
    public Task PlayNextAsync() => PlayNextCoreAsync(wrap: IsWrapMode);

    /// <summary>跳到上一条：进度超过阈值先回到开头，否则切条目。</summary>
    [RelayCommand]
    public async Task PlayPreviousAsync()
    {
        // 成熟播放器惯例：刚播了一会儿时「上一条」语义为回到开头。
        if (Position > RestartThreshold)
        {
            Seek(TimeSpan.Zero);
            return;
        }

        if (_queue.Count == 0 || _isSwitchingItem)
        {
            return;
        }

        var previous = _queueIndex - 1;

        if (previous < 0)
        {
            if (!IsWrapMode)
            {
                return;
            }

            previous = _queue.Count - 1;
        }

        await PlayItemAtAsync(previous);
    }

    /// <summary>进入临时倍速（长按快进）：记住用户倍率后提速，重复调用幂等。</summary>
    public void BeginTemporaryRate()
    {
        if (_isTemporaryRate)
        {
            return;
        }

        _isTemporaryRate = true;
        _userRate = PlaybackRate;
        SetPlaybackRate(TemporaryRate);
    }

    /// <summary>退出临时倍速：恢复用户设定倍率，重复调用幂等。</summary>
    public void EndTemporaryRate()
    {
        if (!_isTemporaryRate)
        {
            return;
        }

        _isTemporaryRate = false;
        SetPlaybackRate(_userRate);
    }

    /// <summary>连播核心：取下一条索引后装载起播，供手动按钮与自动连播共用。</summary>
    private async Task PlayNextCoreAsync(bool wrap)
    {
        if (_queue.Count == 0 || _isSwitchingItem)
        {
            return;
        }

        var next = _queueIndex + 1;

        if (next >= _queue.Count)
        {
            if (!wrap)
            {
                return;
            }

            next = 0;
        }

        await PlayItemAtAsync(next);
    }

    /// <summary>装载指定索引的条目并起播；错误时停留错误态不起播。</summary>
    private async Task PlayItemAtAsync(int index)
    {
        if (_isSwitchingItem || _player is null || (uint)index >= (uint)_queue.Count)
        {
            return;
        }

        _isSwitchingItem = true;

        try
        {
            _queueIndex = index;
            await LoadAsync(_queue[index], _player);

            if (!HasError)
            {
                Play();
            }
        }
        finally
        {
            _isSwitchingItem = false;
            NotifyNavigationStateChanged();
        }
    }

    /// <summary>随机打乱队列：当前条目固定到头部，其余 Fisher–Yates 洗牌，保证接下来按打乱序顺序播放。</summary>
    private void ShuffleQueue()
    {
        var list = _queue.ToList();

        if (list.Count <= 1)
        {
            return;
        }

        // 先把当前项移到头部：随机模式不中断正在播放的条目。
        var current = list[_queueIndex];
        list.RemoveAt(_queueIndex);
        list.Insert(0, current);

        for (var i = list.Count - 1; i > 1; i--)
        {
            var j = Random.Shared.Next(1, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        _queue = list;
        _queueIndex = 0;
    }

    /// <summary>导航可用性与倍速相关通知统一收口。</summary>
    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }
}
