/**
 * 设置页视图模型（M2）。
 * 职责：管理媒体库扫描源的增删启停与索引扫描进度（添加成功后自动索引新源），
 *      并在索引完成后发起后台元数据回填，以及主题、幻灯片与图片查看器行为等界面偏好。
 * 复用约定：设置变更先写入 ISettingsService 持久化，再通知外壳应用；
 *          扫描走 MediaIndexingService 后台任务，进度通过 IProgress 上报到界面；
 *          元数据回填走 MediaMetadataBackfillService，与扫描的进度体系相互独立。
 * 关键约束：扫描期间禁止再次启动扫描，否则两个任务会同时写入同一批路径；
 *          移除扫描源时必须同步清理其下的索引条目，否则会留下无法访问却又可见的僵尸记录；
 *          路径来自用户选择，入库前必须经 PathGuard 规范化，保证与索引时的前缀一致；
 *          回填结果不影响索引成败，故不纳入扫描的状态流转与错误处理，只作静默后台推进。
 */

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Abstractions;
using System.Net.Sockets;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Core.Utilities;
using Pixbian.Services;
using Pixbian.WebServer;
using Pixbian.WebServer.Security;

namespace Pixbian.ViewModels;

/// <summary>设置页视图模型。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ILibraryFolderRepository _libraryFolders;
    private readonly IMediaItemRepository _mediaItems;
    private readonly MediaIndexingService _indexingService;
    private readonly MediaMetadataBackfillService _metadataBackfill;
    private readonly ISettingsService _settings;
    private readonly IMusicLibraryService _musicLibrary;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<WebAccessServer> _webServerFactory;
    private WebAccessServer? _webServer;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private int _indexedCount;

    /// <summary>回填是否已在运行（互锁标志，1 表示在跑）。</summary>
    private int _backfillRunning;

    /// <summary>常驻续跑循环是否已启动（进程内一份即可，重复启动会空转多份循环）。</summary>
    private int _residencyStarted;

    /// <summary>常驻续跑的首轮启动延迟：避开应用首屏的缩略图解码与磁盘缓存扫描的 IO 竞争。</summary>
    private static readonly TimeSpan BackfillStartupDelay = TimeSpan.FromSeconds(8);

    /// <summary>常驻续跑的轮间间隔：一轮 25 批跑完后给前台留出无争抢窗口再推进下一轮。</summary>
    private static readonly TimeSpan BackfillResidencyInterval = TimeSpan.FromMinutes(1);

    [ObservableProperty]
    private string _webStatusText = "未启用";

    /// <summary>已启动时可点击访问的地址列表；未启用或启动失败时为空。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _webAccessUrls = [];

    [ObservableProperty]
    private string _musicStatusText = "尚未添加音乐目录";

    /// <summary>图库文件夹计数的展示文本；承载「图库位置」行的副标题。</summary>
    [ObservableProperty]
    private string _folderCountText = "尚未添加文件夹";

    /// <summary>最近一次索引完成时间的展示文本；承载「刷新库」行的默认副标题。</summary>
    [ObservableProperty]
    private string _lastIndexText = "尚未索引";

    public SettingsViewModel(
        ILibraryFolderRepository libraryFolders,
        IMediaItemRepository mediaItems,
        MediaIndexingService indexingService,
        MediaMetadataBackfillService metadataBackfill,
        ISettingsService settings,
        IMusicLibraryService musicLibrary,
        Func<WebAccessServer> webServerFactory,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(indexingService);
        ArgumentNullException.ThrowIfNull(metadataBackfill);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(musicLibrary);

        _libraryFolders = libraryFolders;
        _mediaItems = mediaItems;
        _indexingService = indexingService;
        _metadataBackfill = metadataBackfill;
        _settings = settings;
        _musicLibrary = musicLibrary;
        _webServerFactory = webServerFactory;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

        // 「刷新库」行副标题是三个来源属性的计算值：任一变化都联动通知（不依赖生成器回调，
        // 规避 partial hook 签名与源生成器版本的耦合）。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsIndexing) or nameof(StatusText) or nameof(LastIndexText))
            {
                OnPropertyChanged(nameof(IndexLineText));
            }
        };
    }

    /// <summary>扫描源集合。</summary>
    public ObservableCollection<LibraryFolderRow> Folders { get; } = [];

    /// <summary>音乐库目录集合；与图库扫描源相互独立，只服务短片页的背景音乐。</summary>
    public ObservableCollection<MusicFolderRow> MusicFolders { get; } = [];

    /// <summary>「刷新库」行副标题：索引进行中跟随进度文字，默认显示上次索引时间。</summary>
    public string IndexLineText => IsIndexing ? StatusText : LastIndexText;

    /// <summary>当前主题。</summary>
    public AppTheme Theme
    {
        get => _settings.Current.Theme;
        set
        {
            if (_settings.Current.Theme == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { Theme = value });
            OnPropertyChanged();
        }
    }

    /// <summary>幻灯片播放间隔（秒）。</summary>
    public int SlideShowIntervalSeconds
    {
        get => _settings.Current.SlideShowIntervalSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 3600);

            if (_settings.Current.SlideShowIntervalSeconds == clamped)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowIntervalSeconds = clamped });
            OnPropertyChanged();
        }
    }

    /// <summary>图片查看器中鼠标滚轮的行为。</summary>
    public ViewerWheelMode ViewerWheelMode
    {
        get => _settings.Current.ViewerWheelMode;
        set
        {
            if (_settings.Current.ViewerWheelMode == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { ViewerWheelMode = value });
            OnPropertyChanged();
        }
    }

    /// <summary>图片打开时的初始缩放方式。</summary>
    public ViewerInitialZoom ViewerInitialZoom
    {
        get => _settings.Current.ViewerInitialZoom;
        set
        {
            if (_settings.Current.ViewerInitialZoom == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { ViewerInitialZoom = value });
            OnPropertyChanged();
        }
    }

    /// <summary>幻灯片播放时选取下一张的顺序。</summary>
    public SlideShowPlayOrder SlideShowOrder
    {
        get => _settings.Current.SlideShowOrder;
        set
        {
            if (_settings.Current.SlideShowOrder == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowOrder = value });
            OnPropertyChanged();
        }
    }

    /// <summary>幻灯片切换图片时的过渡方式。</summary>
    public SlideShowTransitionMode SlideShowTransition
    {
        get => _settings.Current.SlideShowTransition;
        set
        {
            if (_settings.Current.SlideShowTransition == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowTransition = value });
            OnPropertyChanged();
        }
    }

    /// <summary>是否播放完整视频；关闭时按截取片段策略播放。</summary>
    public bool SlideShowFullVideoPlayback
    {
        get => _settings.Current.SlideShowFullVideoPlayback;
        set
        {
            if (_settings.Current.SlideShowFullVideoPlayback == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowFullVideoPlayback = value });
            OnPropertyChanged();
        }
    }

    /// <summary>静音播放开关：是否启用背景音乐体系；关闭时放映完全无声。</summary>
    public bool SlideShowSilentPlayback
    {
        get => _settings.Current.SlideShowSilentPlayback;
        set
        {
            if (_settings.Current.SlideShowSilentPlayback == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowSilentPlayback = value });
            OnPropertyChanged();
        }
    }

    /// <summary>幻灯片放映的背景音乐模式；仅在静音播放开启时生效。</summary>
    public BackgroundMusicMode SlideShowBackgroundMusic
    {
        get => _settings.Current.SlideShowBackgroundMusic;
        set
        {
            if (_settings.Current.SlideShowBackgroundMusic == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowBackgroundMusic = value });
            OnPropertyChanged();
        }
    }

    /// <summary>背景音乐音量（0–1），设置界面以百分比呈现。</summary>
    public double SlideShowBackgroundMusicVolume
    {
        get => _settings.Current.SlideShowBackgroundMusicVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);

            if (_settings.Current.SlideShowBackgroundMusicVolume == clamped)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowBackgroundMusicVolume = clamped });
            OnPropertyChanged();
        }
    }

    /// <summary>幻灯片放映的画面动画效果。</summary>
    public SlideShowAnimationMode SlideShowAnimation
    {
        get => _settings.Current.SlideShowAnimation;
        set
        {
            if (_settings.Current.SlideShowAnimation == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowAnimation = value });
            OnPropertyChanged();
        }
    }

    /// <summary>截取片段上限的合法档位（秒）。</summary>
    private static readonly int[] ClipPresetOptions = { 10, 30, 60, 90, 120 };

    /// <summary>截取片段的时长上限（秒），合法值 10/30/60/90/120，非法值回落 60。</summary>
    public int SlideShowClipPresetSeconds
    {
        get => _settings.Current.SlideShowClipPresetSeconds;
        set
        {
            var clamped = ClipPresetOptions.Contains(value) ? value : 60;

            if (_settings.Current.SlideShowClipPresetSeconds == clamped)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { SlideShowClipPresetSeconds = clamped });
            OnPropertyChanged();
        }
    }

    /// <summary>设置变更后的回调，供外壳重新应用主题与视图配置。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>当前设置快照。</summary>
    public AppSettings Settings => _settings.Current;

    /// <summary>加载扫描源列表。</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var folders = await _libraryFolders.GetAllAsync();

        // GetAllAsync 内部使用 ConfigureAwait(false)，切回 UI 线程后再改绑定集合。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            Folders.Clear();

            foreach (var folder in folders)
            {
                Folders.Add(new LibraryFolderRow(folder));
            }

            FolderCountText = Folders.Count == 0
                ? "尚未添加文件夹"
                : $"共 {Folders.Count} 个文件夹";
            RefreshLastIndexText();
        });
    }

    /// <summary>加载音乐库目录列表。</summary>
    [RelayCommand]
    public async Task LoadMusicFoldersAsync()
    {
        var paths = _settings.Current.MusicLibraryPaths;

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            MusicFolders.Clear();

            foreach (var path in paths)
            {
                MusicFolders.Add(new MusicFolderRow(path));
            }

            MusicStatusText = MusicFolders.Count == 0
                ? "尚未添加音乐目录"
                : $"共 {MusicFolders.Count} 个音乐目录";
        });
    }

    /// <summary>添加音乐目录：保存后立即重扫，让新目录下的曲目尽快可用。</summary>
    /// <param name="path">目录路径。</param>
    [RelayCommand]
    public async Task AddMusicFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;

        try
        {
            // 与 JsonSettingsService.NormalizeMusicPaths 同口径：绝对路径且不带结尾分隔符，
            // 否则同一个目录会被判成两条记录。
            normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            MusicStatusText = "路径无效，请重新选择。";
            return;
        }

        var current = _settings.Current.MusicLibraryPaths;

        if (current.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            MusicStatusText = "该目录已在音乐库中。";
            return;
        }

        var updated = _settings.Current with { MusicLibraryPaths = [.. current, normalized] };
        await SaveMusicSettingsAsync(updated);

        var count = await _musicLibrary.ScanAsync();
        MusicStatusText = $"已添加并完成扫描，音乐库共 {count} 首";
    }

    /// <summary>移除音乐目录：其下曲目经重扫后自然从音乐库消失。</summary>
    /// <param name="row">目标目录行。</param>
    [RelayCommand]
    public async Task RemoveMusicFolderAsync(MusicFolderRow? row)
    {
        if (row is null)
        {
            return;
        }

        var updated = _settings.Current with
        {
            MusicLibraryPaths = _settings.Current.MusicLibraryPaths
                .Where(p => !string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase))
                .ToList()
        };

        await SaveMusicSettingsAsync(updated);

        var count = await _musicLibrary.ScanAsync();
        MusicStatusText = $"已移除：{row.Path}，音乐库剩余 {count} 首";
    }

    /// <summary>保存音乐库设置：落盘、广播变更并刷新界面列表。</summary>
    /// <param name="settings">新的设置快照。</param>
    private async Task SaveMusicSettingsAsync(AppSettings settings)
    {
        await _settings.SaveAsync(settings);
        SettingsChanged?.Invoke(this, settings);
        await LoadMusicFoldersAsync();
    }

    /// <summary>添加扫描源，成功后立即索引新添加的文件夹，让新增媒体尽快可用。</summary>
    /// <param name="path">目录路径。</param>
    [RelayCommand]
    public async Task AddFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;

        try
        {
            normalized = PathGuard.NormalizeDirectory(path);
            await _libraryFolders.AddAsync(normalized);
            await LoadAsync();
        }
        catch (ArgumentException ex)
        {
            StatusText = $"路径无效：{ex.Message}";
            return;
        }

        var added = Folders.FirstOrDefault(
            f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));

        if (added is null)
        {
            return;
        }

        // 已有全量索引在跑时不再叠加单源扫描，避免两个任务并发写同一批路径；
        // 新增文件夹可稍后用「立即索引」补扫。
        if (IsIndexing)
        {
            StatusText = "已有索引任务进行中，新增文件夹可稍后用「立即索引」扫描。";
            return;
        }

        IsIndexing = true;
        IndexedCount = 0;

        try
        {
            await ScanFolderAsync(added);
            StatusText = $"已添加并完成索引：{added.DisplayName}";
            StartMetadataBackfillAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
                                      or UnauthorizedAccessException)
        {
            StatusText = "索引中断，请检查目录权限后重试";
        }
        finally
        {
            IsIndexing = false;
            NotifyIndexingStateChanged();
        }
    }

    /// <summary>在指定扫描源下创建子文件夹；空文件夹不产生索引条目，无需触发扫描。</summary>
    /// <param name="args">目标扫描源与新子文件夹名称。</param>
    [RelayCommand]
    public async Task CreateSubFolderAsync((LibraryFolderRow? Row, string? Name) args)
    {
        var (row, name) = args;

        if (row is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var target = Path.Combine(row.Path, name.Trim());

        if (Directory.Exists(target))
        {
            StatusText = "同名文件夹已存在。";
            return;
        }

        try
        {
            await Task.Run(() => Directory.CreateDirectory(target));
            StatusText = $"已创建文件夹：{name.Trim()}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"创建失败：{ex.Message}";
        }
    }

    /// <summary>重命名扫描源：磁盘目录改名后迁移库记录，清理旧索引条目并按新路径重新扫描。</summary>
    /// <param name="args">目标扫描源与新目录名。</param>
    [RelayCommand(CanExecute = nameof(CanStartIndexing))]
    public async Task RenameFolderAsync((LibraryFolderRow? Row, string? Name) args)
    {
        var (row, newName) = args;

        if (row is null || IsIndexing || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var rawNewPath = Path.Combine(
            Path.GetDirectoryName(row.Path) ?? string.Empty,
            newName.Trim());

        // 入库前必须规范化，与 AddFolderAsync 保持同一口径：库里的路径都是规范化后的形态，
        // 直接用拼接结果入库会让索引的前缀比对失配，留下无法对账的僵尸记录。
        string newPath;

        try
        {
            newPath = PathGuard.NormalizeDirectory(rawNewPath);
        }
        catch (ArgumentException ex)
        {
            StatusText = $"路径无效：{ex.Message}";
            return;
        }

        if (string.Equals(newPath, row.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Task.Run(() => Directory.Move(row.Path, rawNewPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"重命名失败：{ex.Message}";
            return;
        }

        // 旧路径下的索引条目整体失效：删除后按新路径入库重建，扫描自带对账保证一致。
        var stalePaths = await _mediaItems.GetPathsUnderDirectoryAsync(row.Path);

        if (stalePaths.Count > 0)
        {
            await _mediaItems.DeleteByPathsAsync(stalePaths);
        }

        await _libraryFolders.RemoveAsync(row.Folder.Id);
        await _libraryFolders.AddAsync(newPath);
        await LoadAsync();

        var renamed = Folders.FirstOrDefault(
            f => string.Equals(f.Path, newPath, StringComparison.OrdinalIgnoreCase));

        if (renamed is null)
        {
            return;
        }

        IsIndexing = true;
        NotifyIndexingStateChanged();

        try
        {
            await ScanFolderAsync(renamed);
            StatusText = $"已重命名并完成索引：{renamed.DisplayName}";
            StartMetadataBackfillAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
                                      or UnauthorizedAccessException)
        {
            StatusText = "重命名完成，但索引中断，请稍后用「立即索引」补扫。";
        }
        finally
        {
            IsIndexing = false;
            NotifyIndexingStateChanged();
        }
    }

    /// <summary>删除扫描源文件夹：整个文件夹移入系统回收站，并清理图库索引与扫描源记录。</summary>
    /// <param name="row">目标扫描源。</param>
    [RelayCommand(CanExecute = nameof(CanStartIndexing))]
    public async Task DeleteFolderAsync(LibraryFolderRow? row)
    {
        if (row is null || IsIndexing)
        {
            return;
        }

        var removed = await Task.Run(() => RecycleBinHelper.SendToRecycleBin(row.Path));

        if (!removed)
        {
            StatusText = "删除失败：文件夹可能正被占用，请关闭相关程序后重试。";
            return;
        }

        var staleCount = await RemoveFolderAsync(row);
        StatusText = $"已删除文件夹（移入回收站），并清理 {staleCount} 条索引。";
    }

    /// <summary>对单个扫描源执行索引扫描并回写扫描时间；供「立即索引」与自动索引共用。</summary>
    /// <param name="row">目标扫描源。</param>
    private async Task ScanFolderAsync(LibraryFolderRow row)
    {
        StatusText = $"正在扫描：{row.Folder.Path}";

        var progress = new Progress<IndexingProgress>(p =>
        {
            IndexedCount += 1;
            StatusText = $"已处理 {p.ProcessedCount} 个文件";
        });

        var report = await _indexingService.ScanAsync(row.Folder, progress);
        row.UpdateLastScan(report.CompletedUtc);
        RefreshLastIndexText();
    }

    /// <summary>汇总各扫描源的最近扫描时间，刷新「上次索引」展示文本。</summary>
    private void RefreshLastIndexText()
    {
        var last = Folders
            .Where(f => f.LastScanUtc.HasValue)
            .Select(f => f.LastScanUtc!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        LastIndexText = last == DateTimeOffset.MinValue
            ? "尚未索引"
            : $"上次索引：{last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}";
    }

    /// <summary>索引完成后发起后台元数据回填：为新建或重建的索引条目补上宽高与时长。</summary>
    /// <remarks>
    /// 回填与索引刻意解耦：扫描完成时界面已经可用，回填只是让下次打开该文件夹更快，
    /// 故不参与扫描的状态流转、不向用户暴露进度，失败也无需打断用户当前操作。
    /// 已在回填时直接返回而不排队：两个回填任务会各自取到同一批待处理条目
    /// （彼此的更新尚未提交），重复探测同一批文件并在数据库写锁上互相等待，
    /// 叠加的持续文件 IO 会让前台浏览表现为卡死。
    /// 落下的条目由常驻续跑循环（若已启动）或下次索引后的回填接手。
    /// </remarks>
    private void StartMetadataBackfillAsync()
    {
        _ = RunBackfillRoundAsync();
    }

    /// <summary>发起一轮回填（最多 25 批）；返回本轮处理条数，-1 表示因互锁未执行。</summary>
    /// <remarks>
    /// 用互锁标志而非布尔字段做排他：扫描回调、启动流程与常驻循环可能并发进入。
    /// 扫描触发的调用不关心返回值；常驻循环用它区分「清零」与「本轮被占用」。
    /// </remarks>
    private async Task<int> RunBackfillRoundAsync()
    {
        if (Interlocked.CompareExchange(ref _backfillRunning, 1, 0) != 0)
        {
            return -1;
        }

        try
        {
            return await _metadataBackfill.BackfillAllAsync();
        }
        catch (OperationCanceledException)
        {
            // 取消属预期行为，未完成的条目会在下一轮继续推进。
            return -1;
        }
        finally
        {
            Interlocked.Exchange(ref _backfillRunning, 0);
        }
    }

    /// <summary>启动元数据回填的常驻续跑循环（幂等，进程内一份）。</summary>
    /// <remarks>
    /// 断点续跑：每轮回填最多推进 25 批（5000 条），此前落下的条目要等「下次索引」才能继续；
    /// 本循环让余量在应用存续期间按间隔自动推进直至清零。延迟首轮避开启动首屏的
    /// 缩略图解码与磁盘缓存扫描的 IO 竞争；每批独立提交，应用退出时未完成部分随
    /// 进程终止，无状态损坏，下次启动自动接续。
    /// </remarks>
    public void StartBackfillResidency()
    {
        if (Interlocked.CompareExchange(ref _residencyStarted, 1, 0) != 0)
        {
            return;
        }

        _ = RunBackfillResidencyLoopAsync();
    }

    private async Task RunBackfillResidencyLoopAsync()
    {
        try
        {
            await Task.Delay(BackfillStartupDelay);

            while (true)
            {
                var processed = await RunBackfillRoundAsync();

                // 0 = 本轮清零（无待处理条目）：循环退出，之后新增扫描源经扫描流程触发。
                // -1 = 本轮被扫描触发的回填占用互锁（或取消）：让出，按间隔重试。
                if (processed == 0)
                {
                    return;
                }

                await Task.Delay(BackfillResidencyInterval);
            }
        }
        catch (Exception)
        {
            // 数据库等基础设施异常时终止常驻循环：反复重试只会持续占盘且必然失败，
            // 落下的条目由下次索引或下次启动接手。静默即可，不打扰用户。
        }
    }


    /// <summary>扫描任务进入或退出后统一刷新相关命令的可用状态。</summary>
    private void NotifyIndexingStateChanged()
    {
        StartIndexingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>移除扫描源并清理其下的索引条目；返回清理的索引条目数。</summary>
    /// <param name="row">待移除的扫描源。</param>
    [RelayCommand]
    public async Task<int> RemoveFolderAsync(LibraryFolderRow? row)
    {
        if (row is null)
        {
            return 0;
        }

        var stalePaths = await _mediaItems.GetPathsUnderDirectoryAsync(row.Folder.Path);

        if (stalePaths.Count > 0)
        {
            await _mediaItems.DeleteByPathsAsync(stalePaths);
        }

        await _libraryFolders.RemoveAsync(row.Folder.Id);
        await LoadAsync();

        StatusText = $"已移除扫描源并清理 {stalePaths.Count} 条索引";
        return stalePaths.Count;
    }

    /// <summary>对全部启用的扫描源执行索引扫描。</summary>
    [RelayCommand(CanExecute = nameof(CanStartIndexing))]
    public async Task StartIndexingAsync()
    {
        if (IsIndexing)
        {
            return;
        }

        IsIndexing = true;
        NotifyIndexingStateChanged();
        IndexedCount = 0;

        try
        {
            foreach (var row in Folders.Where(f => f.Folder.IsEnabled).ToList())
            {
                await ScanFolderAsync(row);
            }

            StatusText = "索引完成";
            StartMetadataBackfillAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
                                      or UnauthorizedAccessException)
        {
            StatusText = "索引中断，请检查目录权限后重试";
        }
        finally
        {
            IsIndexing = false;
            NotifyIndexingStateChanged();
        }
    }

    private bool CanStartIndexing() => !IsIndexing;

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        await _settings.SaveAsync(settings);
        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>应用局域网 Web 访问配置：保存设置并按需启停服务。</summary>
    /// <param name="isEnabled">是否启用。</param>
    /// <param name="port">监听端口。</param>
    /// <param name="newPassword">新密码；为空白表示沿用既有哈希。</param>
    public async Task ApplyWebSharingAsync(bool isEnabled, int port, string? newPassword)
    {
        var hash = _settings.Current.WebPasswordHash;

        if (isEnabled && !string.IsNullOrWhiteSpace(newPassword))
        {
            // 设置了新密码就重新计算哈希，旧哈希立即失效。
            hash = AuthService.HashPassword(newPassword);
        }

        var updated = _settings.Current with
        {
            IsWebSharingEnabled = isEnabled,
            WebSharingPort = Math.Clamp(port, 1024, 65535),
            WebPasswordHash = hash
        };

        await _settings.SaveAsync(updated);
        SettingsChanged?.Invoke(this, updated);

        // 端口或密码变更都需要重建服务器实例（AuthService 不可变）。
        if (_webServer is not null)
        {
            await _webServer.StopAsync();
            await _webServer.DisposeAsync();
            _webServer = null;
        }

        if (!isEnabled)
        {
            WebStatusText = "未启用";
            WebAccessUrls = [];
            return;
        }

        try
        {
            _webServer = _webServerFactory();
            await _webServer.StartAsync();

            // 状态文字与地址分离：地址由界面渲染为可点击链接，纯文本拼接无法承载点击语义。
            WebStatusText = "已启动，点击地址可在浏览器中打开：";
            WebAccessUrls = _webServer.ActiveUrls;
        }
        catch (Exception ex) when (ex is SocketException
                                      or IOException
                                      or InvalidOperationException
                                      or ArgumentOutOfRangeException)
        {
            WebStatusText = "启动失败：端口可能被占用，请更换端口后重试。";
            WebAccessUrls = [];
        }
    }
}

/// <summary>扫描源列表行，用于在界面上呈现并可就地更新扫描时间。</summary>
public sealed partial class LibraryFolderRow : ObservableObject
{
    [ObservableProperty]
    private DateTimeOffset? _lastScanUtc;

    public LibraryFolderRow(LibraryFolder folder)
    {
        Folder = folder;
        _lastScanUtc = folder.LastScanUtc;
    }

    /// <summary>底层扫描源。</summary>
    public LibraryFolder Folder { get; }

    /// <summary>目录路径。</summary>
    public string Path => Folder.Path;

    /// <summary>展示名称。</summary>
    public string DisplayName => Folder.DisplayName;

    /// <summary>是否启用。</summary>
    public bool IsEnabled => Folder.IsEnabled;

    /// <summary>最近扫描时间的展示文本。</summary>
    public string LastScanText =>
        LastScanUtc.HasValue
            ? LastScanUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "尚未扫描";

    /// <summary>更新扫描时间并刷新展示文本。</summary>
    public void UpdateLastScan(DateTimeOffset scannedUtc)
    {
        LastScanUtc = scannedUtc;
        OnPropertyChanged(nameof(LastScanText));
    }
}

/// <summary>音乐库目录行，用于在界面上呈现并可就地移除。</summary>
public sealed class MusicFolderRow
{
    /// <summary>初始化目录行。</summary>
    /// <param name="path">目录完整路径。</param>
    public MusicFolderRow(string path)
    {
        Path = path;
    }

    /// <summary>目录完整路径。</summary>
    public string Path { get; }
}
