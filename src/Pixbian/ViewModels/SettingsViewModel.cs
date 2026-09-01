/**
 * 设置页视图模型（M2）。
 * 职责：管理媒体库扫描源的增删启停与索引扫描进度（添加成功后自动索引新源），
 *      并在索引完成后发起后台元数据回填，以及主题、视图模式、缩略图尺寸等界面偏好。
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
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<WebAccessServer> _webServerFactory;
    private WebAccessServer? _webServer;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private int _indexedCount;

    [ObservableProperty]
    private string _webStatusText = "未启用";

    public SettingsViewModel(
        ILibraryFolderRepository libraryFolders,
        IMediaItemRepository mediaItems,
        MediaIndexingService indexingService,
        MediaMetadataBackfillService metadataBackfill,
        ISettingsService settings,
        Func<WebAccessServer> webServerFactory,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(indexingService);
        ArgumentNullException.ThrowIfNull(metadataBackfill);
        ArgumentNullException.ThrowIfNull(settings);

        _libraryFolders = libraryFolders;
        _mediaItems = mediaItems;
        _indexingService = indexingService;
        _metadataBackfill = metadataBackfill;
        _settings = settings;
        _webServerFactory = webServerFactory;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>扫描源集合。</summary>
    public ObservableCollection<LibraryFolderRow> Folders { get; } = [];

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

    /// <summary>当前视图模式。</summary>
    public GalleryViewMode ViewMode
    {
        get => _settings.Current.ViewMode;
        set
        {
            if (_settings.Current.ViewMode == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { ViewMode = value });
            OnPropertyChanged();
        }
    }

    /// <summary>当前缩略图边长（像素）。</summary>
    public int ThumbnailSize
    {
        get => _settings.Current.ThumbnailSize;
        set
        {
            if (_settings.Current.ThumbnailSize == value)
            {
                return;
            }

            _ = SaveSettingsAsync(_settings.Current with { ThumbnailSize = value });
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

            StatusText = $"共 {Folders.Count} 个扫描源";
        });
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

        var newPath = Path.Combine(
            Path.GetDirectoryName(row.Path) ?? string.Empty,
            newName.Trim());

        if (string.Equals(newPath, row.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Task.Run(() => Directory.Move(row.Path, newPath));
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
    }

    /// <summary>发起后台元数据回填：为新建或重建的索引条目补上宽高与时长。</summary>
    /// <remarks>
    /// 回填与索引刻意解耦：扫描完成时界面已经可用，回填只是让下次打开该文件夹更快，
    /// 故不参与扫描的状态流转、不向用户暴露进度，失败也无需打断用户当前操作。
    /// </remarks>
    private void StartMetadataBackfillAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _metadataBackfill.BackfillAllAsync();
            }
            catch (OperationCanceledException)
            {
                // 取消属预期行为，未完成的条目会在下次索引后继续推进。
            }
        });
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
            return;
        }

        try
        {
            _webServer = _webServerFactory();
            await _webServer.StartAsync();

            WebStatusText = $"已启动：{string.Join("  ", _webServer.ActiveUrls)}";
        }
        catch (Exception ex) when (ex is SocketException
                                      or IOException
                                      or InvalidOperationException
                                      or ArgumentOutOfRangeException)
        {
            WebStatusText = "启动失败：端口可能被占用，请更换端口后重试。";
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
