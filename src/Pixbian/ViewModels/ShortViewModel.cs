/**
 * 短片页视图模型。
 * 职责：维护随机视频播放列表、按规则裁出片段并驱动播放，无音轨视频配背景音乐。
 * 复用约定：视频候选经 IMediaItemRepository.QueryAsync 一次取回（按类型 + 随机种子）；
 *          片段区间由 ShortClipPlanner 裁决，解码源统一走 IVideoPlaybackItemFactory（与播放器页共用策略）；
 *          片段结束用 DispatcherQueueTimer 轮询判定，不订阅 PositionChanged——后者会与用户输入打架。
 * 关键约束：本类 await 一律不带 ConfigureAwait(false)——MediaPlayer 的 Source/Play 必须在 UI 线程调用，
 *          落到线程池线程时 Play() 会同步挂起（不返回不抛异常），执行流无声消失；
 *          视频与背景音乐必须同步暂停/结束/切换；切换前必须释放上一个播放项；
 *          时长未知时按整段播放、以 MediaEnded 收尾，不得用 0 时长参与片段规划。
 */

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Media.Models;
using Pixbian.Media.Services;
using Pixbian.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Pixbian.ViewModels;

/// <summary>短片页视图模型。</summary>
public sealed partial class ShortViewModel : ObservableObject, IDisposable
{
    /// <summary>片段结束的轮询间隔。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>单次取回的视频候选上限；远超实际视频规模的保险值。</summary>
    private const int CandidateLimit = 5000;

    /// <summary>背景音乐音量：低于视频原音，避免盖过画面人声。</summary>
    private const double MusicVolume = 0.6;

    /// <summary>载入各步骤的超时：超时即判失败并恢复切换能力——
    /// 绝不允许一次挂起的载入把 _isLoading 永久锁死（那会让切换功能整体失效）。</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>片段终点的判定容差：Position 停在终点前一帧是媒体管线常态
    /// （最后帧 pts ≠ 容器时长，可差数十至数百毫秒），严格比较会永不命中，
    /// 实测表现为视频播完定格最后一帧且不自动切换。</summary>
    private static readonly TimeSpan EndTolerance = TimeSpan.FromMilliseconds(500);

    /// <summary>旧播放项的延迟释放时长：MediaPlayer.Source 切换后媒体引擎对旧源的卸载
    /// 是异步的，立即 Dispose 会与引擎卸载竞态，实测令 UI 线程被 native 等待拖住约 6 秒
    /// （画面定格最后一帧）。留足卸载时间后再在后台释放。</summary>
    private static readonly TimeSpan StaleReleaseDelay = TimeSpan.FromSeconds(5);

    private readonly IMediaItemRepository _mediaItems;
    private readonly IVideoMetadataReader _metadataReader;
    private readonly IVideoPlaybackItemFactory _playbackItemFactory;
    private readonly IMusicLibraryService _musicLibrary;
    private readonly Random _random = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _progressTimer;

    [ObservableProperty]
    private MediaItem? _currentItem;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>视频载入进行中：驱动底部载入指示条显隐；期间切换请求被挡属预期。</summary>
    [ObservableProperty]
    private bool _isLoading;

    private MediaPlayer? _player;
    private MediaPlayer? _musicPlayer;
    private VideoPlaybackItem? _playback;

    /// <summary>背景音乐的播放项；与视频播放项分开持有，停止音乐时单独释放。</summary>
    private VideoPlaybackItem? _musicPlayback;

    /// <summary>按路径缓存的视频元数据：AV1 后端判定、音轨判定与时长都依赖它，
    /// 候选池越大同一文件越会反复命中，不能每次切换都创建 MediaClip 读盘
    /// （机械盘上单次可达数百毫秒，是切换卡顿的直接贡献者）。</summary>
    private readonly Dictionary<string, VideoMetadata?> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private List<MediaItem> _playlist = [];
    private int _playlistIndex = -1;
    private ShortClip _clip;
    private bool _hasAudioTrack;

    /// <summary>初始化短片页视图模型。</summary>
    /// <param name="mediaItems">媒体条目仓储，用于取视频候选。</param>
    /// <param name="metadataReader">视频元数据读取器，用于判定音轨与时长。</param>
    /// <param name="playbackItemFactory">播放项工厂。</param>
    /// <param name="musicLibrary">音乐库服务，提供背景音乐候选。</param>
    /// <param name="dispatcherQueue">调度队列；为空时取当前线程队列。</param>
    public ShortViewModel(
        IMediaItemRepository mediaItems,
        IVideoMetadataReader metadataReader,
        IVideoPlaybackItemFactory playbackItemFactory,
        IMusicLibraryService musicLibrary,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(playbackItemFactory);
        ArgumentNullException.ThrowIfNull(musicLibrary);

        _mediaItems = mediaItems;
        _metadataReader = metadataReader;
        _playbackItemFactory = playbackItemFactory;
        _musicLibrary = musicLibrary;

        var queue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _dispatcherQueue = queue;
        _progressTimer = queue.CreateTimer();
        _progressTimer.Interval = ProgressInterval;
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += OnProgressTick;
    }

    /// <summary>静音按钮图标。</summary>
    public string MuteGlyph => IsMuted ? "\uE74F" : "\uE767";

    /// <summary>载入候选并起播当前条目；重复进入页面时复用既有播放列表。</summary>
    /// <param name="player">由界面提供的播放器实例。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StartAsync(MediaPlayer player, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        _player = player;

        // 页面为单例、播放器每次进入重建，事件先减后加避免重复订阅。
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaEnded += OnMediaEnded;
        _player.IsMuted = IsMuted;

        await EnsurePlaylistAsync(cancellationToken);

        if (_playlist.Count == 0)
        {
            HasError = true;
            StatusText = "图库中还没有视频，先在设置里添加扫描源并完成索引。";
            return;
        }

        if (_playlistIndex < 0)
        {
            _playlistIndex = 0;
        }

        await PlayCurrentAsync(cancellationToken);
    }

    /// <summary>切换播放与暂停；背景音乐随之暂停或恢复。</summary>
    [RelayCommand]
    public void TogglePlayPause()
    {
        if (_player is null)
        {
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            _musicPlayer?.Pause();
            IsPlaying = false;
            _progressTimer.Stop();
            return;
        }

        _player.Play();

        // 只有无音轨的视频才配背景音乐：有音轨时恢复视频自身的声音即可。
        if (!_hasAudioTrack)
        {
            _musicPlayer?.Play();
        }

        IsPlaying = true;
        _progressTimer.Start();
    }

    /// <summary>跳转下一个视频；一轮播完后重新洗牌。</summary>
    [RelayCommand]
    public async Task GoNextAsync()
    {
        // 入口与 guard 拒绝都留痕：「点了没反应」必须能区分是点击未到达还是被载入锁挡住。
        if (IsLoading)
        {
            Diagnostics.Log("SHORTCLIP|next=skip|reason=loading");
            return;
        }

        if (_playlist.Count == 0)
        {
            Diagnostics.Log("SHORTCLIP|next=skip|reason=候选池为空");
            return;
        }

        if (_playlistIndex + 1 >= _playlist.Count)
        {
            ShufflePlaylist();
        }

        _playlistIndex++;
        await PlayCurrentAsync();
    }

    /// <summary>跳转上一个视频；已在首个时回绕到末尾。</summary>
    [RelayCommand]
    public async Task GoPreviousAsync()
    {
        if (IsLoading)
        {
            Diagnostics.Log("SHORTCLIP|prev=skip|reason=loading");
            return;
        }

        if (_playlist.Count == 0)
        {
            Diagnostics.Log("SHORTCLIP|prev=skip|reason=候选池为空");
            return;
        }

        _playlistIndex = _playlistIndex <= 0 ? _playlist.Count - 1 : _playlistIndex - 1;
        await PlayCurrentAsync();
    }

    /// <summary>切换静音；视频与背景音乐一并静音，避免出现「静了画面还有声音」。</summary>
    [RelayCommand]
    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        OnPropertyChanged(nameof(MuteGlyph));

        if (_player is not null)
        {
            _player.IsMuted = IsMuted;
        }

        if (_musicPlayer is not null)
        {
            _musicPlayer.IsMuted = IsMuted;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _progressTimer.Stop();

        StopMusic();

        if (_musicPlayer is not null)
        {
            _musicPlayer.MediaFailed -= OnMusicMediaFailed;
            _musicPlayer.Dispose();
            _musicPlayer = null;
        }

        _playback?.Dispose();
        _playback = null;

        if (_player is null)
        {
            return;
        }

        _player.MediaEnded -= OnMediaEnded;
        _player.Pause();
        _player.Source = null;
        _player.Dispose();
        _player = null;

        IsPlaying = false;
    }

    /// <summary>确保候选池非空；已有时直接复用，避免重复查库。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task EnsurePlaylistAsync(CancellationToken cancellationToken)
    {
        if (_playlist.Count > 0)
        {
            return;
        }

        // 随机排序由固定种子生成，换种子即换一轮顺序；
        // 视频规模远小于图片，一次取回即可，无需游标分页。
        var query = new MediaQuery
        {
            Kind = MediaKind.Video,
            SortKey = MediaSortKey.Random,
            RandomSeed = _random.Next(),
            Take = CandidateLimit
        };

        var items = await _mediaItems.QueryAsync(query, cancellationToken);

        _playlist = [.. items];
        ShufflePlaylist();
    }

    /// <summary>就地洗牌并把游标重置到首项之前。</summary>
    private void ShufflePlaylist()
    {
        for (var i = _playlist.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_playlist[i], _playlist[j]) = (_playlist[j], _playlist[i]);
        }

        _playlistIndex = -1;
    }

    /// <summary>载入并播放当前条目，同时按音轨情况起停背景音乐。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PlayCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_player is null || _playlist.Count == 0)
        {
            return;
        }

        IsLoading = true;
        _progressTimer.Stop();

        // 先停音乐：切换与结束都要求音频立即让位，否则会残留上一首。
        StopMusic();

        var item = _playlist[_playlistIndex];
        CurrentItem = item;
        HasError = false;
        StatusText = "载入中";

        // 切换入口留痕：此行缺失 = GoNextAsync 未被调用（如 MediaEnded 未触发）；
        // 此行有而 t=file 缺失 = 挂在文件打开。两者是自动切换失效的唯一判据。
        Diagnostics.Log($"SHORTCLIP|switch|file={item.FileName}");

        try
        {
            // 切换耗时计时：量化载入各阶段占比，避免凭感觉优化。
            var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var file = await StorageFile.GetFileFromPathAsync(item.Path).AsTask(cancellationToken)
                .WaitAsync(LoadTimeout, cancellationToken);
            Diagnostics.Log($"SHORTCLIP|t=file|{loadStopwatch.ElapsedMilliseconds}ms");

            // 元数据按路径缓存：AV1 后端判定、音轨判定与时长都依赖它；
            // 同一文件在候选池轮转中会反复命中，不能每次切换都重新创建 MediaClip 读盘
            // （机械盘上单次可达数百毫秒，是切换卡顿的直接贡献者）。
            if (!_metadataCache.TryGetValue(item.Path, out var metadata))
            {
                metadata = await _metadataReader.ReadAsync(item.Path, cancellationToken)
                    .WaitAsync(LoadTimeout, cancellationToken);
                _metadataCache[item.Path] = metadata;
            }

            Diagnostics.Log($"SHORTCLIP|t=meta|{loadStopwatch.ElapsedMilliseconds}ms");

            // 元数据读不出来时保守当作无音轨：宁可多放一段背景音乐，也不要整段静音。
            _hasAudioTrack = metadata?.AudioCodec is not null;

            // 「该配乐却没声音」的唯一判据：音轨判定与元数据一并留痕，
            // 否则无从区分「视频被判成有音轨」与「音乐库没抽到曲」两条路径。
            Diagnostics.Log(
                $"SHORTCLIP|hasAudio={_hasAudioTrack}"
                + $"|codec={metadata?.AudioCodec ?? "null"}"
                + $"|channels={metadata?.AudioChannels?.ToString(CultureInfo.InvariantCulture) ?? "null"}"
                + $"|dur={(metadata?.Duration ?? TimeSpan.Zero).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}");

            // FFmpeg 源的创建与释放必须严格串行（全在 UI 线程）：交线程池异步释放会与
            // 下一次创建并发，实测互相锁死令 CreateFromStreamAsync 永久挂起。
            // 旧源在播放器切到新源、完全脱离之后同步释放，串行即安全。
            var stale = _playback;
            _playback = await _playbackItemFactory.CreateAsync(file, metadata)
                .WaitAsync(LoadTimeout, cancellationToken);
            Diagnostics.Log($"SHORTCLIP|t=ffmpeg|{loadStopwatch.ElapsedMilliseconds}ms");

            _player.Source = _playback.Item;
            ScheduleStaleRelease(stale);

            var duration = ResolveDuration(metadata, item);

            // 时长未知时按整段播放：以 TimeSpan.MaxValue 作终点，由 MediaEnded 收尾。
            _clip = duration > TimeSpan.Zero
                ? ShortClipPlanner.Plan(duration, _random)
                : new ShortClip(TimeSpan.Zero, TimeSpan.MaxValue);

            // 定位失败只损失片段起点（退化为整段播放），绝不能阻断播放与背景音乐。
            SeekToClipStart();

            _player.Play();
            IsPlaying = true;
            StatusText = "播放中";
            _progressTimer.Start();

            Diagnostics.Log($"SHORTCLIP|t=total|{loadStopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                      or UnauthorizedAccessException
                                      or IOException
                                      or ArgumentException
                                      or NotSupportedException
                                      or InvalidOperationException
                                      or COMException)
        {
            HasError = true;
            StatusText = "无法播放该文件，可能是编码不受支持或文件已损坏";
            Diagnostics.Log(
                $"SHORTCLIP|load=fail|reason={ex.GetType().Name}|hr=0x{ex.HResult:X8}");
        }
        catch (Exception ex)
        {
            // 兜底：本方法经 fire-and-forget 调用，任何漏网的异常都会被静默丢弃且不留痕迹
            // ——宁可放宽这里的捕获，也不能让载入路径再次变成排查黑洞。
            HasError = true;
            StatusText = "无法播放该文件";
            Diagnostics.Log(
                $"SHORTCLIP|load=fail|unexpected|reason={ex.GetType().Name}|hr=0x{ex.HResult:X8}");
        }
        finally
        {
            IsLoading = false;
        }

        // 背景音乐独立于视频载入：视频侧任何异常都不应连带吞掉音乐，
        // 反过来音乐失败也不影响画面——二者耦合是「该配乐却没声音」最难排查的组合。
        if (!_hasAudioTrack)
        {
            await StartMusicGuardedAsync(cancellationToken);
        }
    }

    /// <summary>启动背景音乐并保证异常不外泄。</summary>
    /// <remarks>
    /// 音乐是陪衬，其自身任何未预期的失败都不许冒泡到调用链——
    /// 本方法经 fire-and-forget 调用，异常一旦漏出会被静默丢弃且不留任何痕迹。
    /// </remarks>
    private async Task StartMusicGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartMusicAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is COMException
                                      or InvalidOperationException
                                      or IOException
                                      or ArgumentException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            Diagnostics.Log($"SHORTBGM|guard|reason={ex.GetType().Name}|hr=0x{ex.HResult:X8}");
        }
    }

    /// <summary>把播放位置移到片段起点；失败仅留痕，不向上抛出。</summary>
    /// <remarks>
    /// FFmpeg 源刚挂上时会话尚未就绪，定位可能抛 WinRT 异常（表现为 COMException）。
    /// 若让它向上传播，会连同 Play 与背景音乐启动一起被外层 catch 吞掉，
    /// 代价远大于丢失片段起点——后者只是退化成整段播放。
    /// </remarks>
    private void SeekToClipStart()
    {
        if (_player is null || _clip.Start <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            _player.PlaybackSession.Position = _clip.Start;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or ArgumentException
                                      or COMException)
        {
            Diagnostics.Log($"SHORTCLIP|seek=fail|reason={ex.GetType().Name}");
        }
    }

    /// <summary>确定视频时长：优先元数据，其次索引库，最后取会话解析到的自然时长。</summary>
    /// <param name="metadata">视频元数据。</param>
    /// <param name="item">媒体条目。</param>
    /// <returns>时长；全部不可用时为 <see cref="TimeSpan.Zero"/>。</returns>
    private TimeSpan ResolveDuration(VideoMetadata? metadata, MediaItem item)
    {
        if (metadata?.Duration is { } probed && probed > TimeSpan.Zero)
        {
            return probed;
        }

        if (item.DurationMs is { } ms && ms > 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        var natural = _player?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
        return natural > TimeSpan.Zero ? natural : TimeSpan.Zero;
    }

    /// <summary>随机抽一首背景音乐并起播；抽不到或播放失败时静默降级为无音乐。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task StartMusicAsync(CancellationToken cancellationToken)
    {
        var picked = _musicLibrary.TakeRandomTrack();

        if (picked is not { } path)
        {
            // 抽不到曲是「无背景音乐」最常见的成因：候选池为空时此前完全静默。
            Diagnostics.Log("SHORTBGM|pick=none|reason=候选池为空");
            return;
        }

        var name = Path.GetFileName(path);

        // 抽到曲必须立即留痕：留痕若放在播放成功之后，
        // 一旦后续任一步抛异常，这一事实会连同异常一起消失，排查时将完全无线索。
        Diagnostics.Log($"SHORTBGM|pick=ok|file={name}");

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken)
                .WaitAsync(LoadTimeout, cancellationToken);

            // 背景音乐与视频统一走 FFmpeg 解码：系统 Media Foundation 对 Ogg Vorbis 一类
            // 格式支持不完整，抽到这类文件时 MediaPlayer 只在内部失败、不抛同步异常，
            // 表现正是「判定该配乐却完全没有声音」。
            _musicPlayback?.Dispose();
            _musicPlayback = await _playbackItemFactory.CreateAsync(file, null)
                .WaitAsync(LoadTimeout, cancellationToken);

            _musicPlayer ??= CreateMusicPlayer();
            _musicPlayer.Source = _musicPlayback.Item;
            _musicPlayer.Play();

            Diagnostics.Log($"SHORTBGM|play=ok|file={name}");
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                      or UnauthorizedAccessException
                                      or IOException
                                      or ArgumentException
                                      or NotSupportedException
                                      or InvalidOperationException
                                      or COMException)
        {
            // 单首失败不影响视频播放：短片页的核心是画面，音乐只是陪衬。
            // 记 HResult：WinRT 异常的类型名区分度低，HRESULT 才是定位依据。
            Diagnostics.Log(
                $"SHORTBGM|play=fail|file={name}|reason={ex.GetType().Name}|hr=0x{ex.HResult:X8}");
        }
    }

    /// <summary>创建背景音乐播放器；不绑定任何界面元素，只输出音频。</summary>
    private MediaPlayer CreateMusicPlayer()
    {
        var player = new MediaPlayer
        {
            // 音频常短于 40~80 秒的片段，开启循环避免提前静下来。
            IsLoopingEnabled = true,
            IsMuted = IsMuted,
            Volume = MusicVolume
        };

        // 音频解码失败只经 MediaFailed 上报，不抛同步异常——
        // 不订阅就完全静默，表现为「抽到了曲却没声音」且无从排查。
        player.MediaFailed += OnMusicMediaFailed;

        return player;
    }

    /// <summary>记录背景音乐的播放失败；音频失败不会在调用处抛异常，只能靠此事件取证。</summary>
    private static void OnMusicMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        Diagnostics.Log($"SHORTBGM|mediafail|error={args.Error}|msg={args.ErrorMessage}");

    /// <summary>停止背景音乐；旧源脱离播放器后延迟释放（见 ScheduleStaleRelease）。</summary>
    private void StopMusic()
    {
        var stale = _musicPlayback;
        _musicPlayback = null;

        if (_musicPlayer is not null)
        {
            _musicPlayer.Pause();
            _musicPlayer.Source = null;
        }

        ScheduleStaleRelease(stale);
    }

    /// <summary>延迟释放已脱离播放器的旧播放项。</summary>
    /// <remarks>
    /// MediaPlayer.Source 切换（含置 null）后，媒体引擎对旧源的卸载是异步的——立即
    /// Dispose 会与引擎卸载竞态，实测令 UI 线程被 native 等待拖住约 6 秒（画面定格）。
    /// 延迟数秒等引擎完成卸载后再在后台释放；Dispose 幂等，重复调用安全。
    /// </remarks>
    private static void ScheduleStaleRelease(VideoPlaybackItem? item)
    {
        if (item is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StaleReleaseDelay).ConfigureAwait(false);
                item.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                Diagnostics.Log($"SHORTCLIP|release=fail|reason={ex.GetType().Name}");
            }
        });
    }

    /// <summary>轮询判定片段是否播完；这是自动切换的主要判定，MediaEnded 仅作兜底。</summary>
    /// <remarks>
    /// 终点判定必须留容差：Position 停在终点前一帧（最后帧 pts ≠ 容器时长）是媒体管线
    /// 常态，严格 >= 会永不命中——实测表现为视频播完定格最后一帧、既不切换也无 MediaEnded。
    /// 代价是每个片段提前至多 500ms 结束，观感无感。时长未知的整段播放仍依赖 MediaEnded。
    /// </remarks>
    private void OnProgressTick(DispatcherQueueTimer sender, object args)
    {
        var session = _player?.PlaybackSession;

        if (session is null || IsLoading || _clip.End == TimeSpan.MaxValue)
        {
            return;
        }

        if (session.Position >= _clip.End - EndTolerance)
        {
            _ = GoNextAsync();
        }
    }

    /// <summary>整段播放到自然结尾：切换到下一个视频。</summary>
    /// <remarks>
    /// 引擎回调上下文里直接启动下一轮载入，实测会让随后的文件打开挂起——
    /// 与 NavigationView「回调内同步改状态会出事」同一类问题：
    /// 一律经 TryEnqueue 延到消息循环的下一个回调再推进切换。
    /// </remarks>
    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        Diagnostics.Log("SHORTCLIP|media-ended");
        _dispatcherQueue.TryEnqueue(() => _ = GoNextAsync());
    }
}
