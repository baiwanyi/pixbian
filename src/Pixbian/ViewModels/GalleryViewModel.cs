/**
 * 图库页视图模型（M2）。
 * 职责：按条件分页加载媒体条目，管理多选、批量收藏、移除索引，并维护当前内容统计与排序筛选。
 * 复用约定：数据访问全部经 IMediaItemRepository，禁止在此编写 SQL 或直接触碰文件系统；
 *          全部命令基于 CommunityToolkit.Mvvm 的 AsyncRelayCommand，自动维护 CanExecute 与并发保护；
 *          列表查询与页头统计共用 CurrentQuery，保证两处条件同源、数字与内容一致。
 * 关键约束：分页为追加模式，切换筛选或搜索时必须先清空集合并把 Skip 归零，否则会串页；
 *          条目为纯平铺，不做日期分组；
 *          从索引移除仅删记录不动磁盘；删除文件经回收站（RecycleBinHelper）逐个移入，期间经通知条
 *          展示进度、支持取消，并原地从集合移除（不整页重载），结束后批量清索引并刷新统计；
 *          缩略图加载失败不得中断列表渲染。
 */

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>图库页视图模型。</summary>
public sealed partial class GalleryViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 200;

    /// <summary>尺寸预取的并发度：只读文件头，并发远快于串行，但过高会与缩略图解码争抢 IO。</summary>
    private const int DimensionPrefetchConcurrency = 4;

    private readonly IMediaItemRepository _mediaItems;
    private readonly IThumbnailService _thumbnails;
    private readonly DispatcherQueue _dispatcherQueue;

    private MediaKind? _kindFilter;
    private bool _onlyFavorites;
    private long? _categoryFilter;
    private string? _categoryName;
    private string? _directoryPath;
    private string? _directoryName;
    private MediaSortKey _sortKey = MediaSortKey.ModifiedDate;
    private SortDirection _sortDirection = SortDirection.Descending;
    private int _randomSeed;
    private string _searchText = string.Empty;
    private int _thumbnailSize = ThumbnailSizes.Default;

    /// <summary>删除结果通知条自动消失的延时。</summary>
    private static readonly TimeSpan DeleteResultAutoCloseDelay = TimeSpan.FromSeconds(5);

    /// <summary>数据库侧累计读取的条目数（分页游标）。删除只收缩界面集合、不回退该游标，
    /// 否则增量分页的 Skip 与数据库偏移错位，已展示的条目会被重复拉取。</summary>
    private int _loadedCount;

    private CancellationTokenSource? _deleteCts;
    private DispatcherQueueTimer? _deleteResultTimer;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasMore = true;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private MediaItemViewModel? _selectedItem;

    [ObservableProperty]
    private int _photoTotal;

    [ObservableProperty]
    private int _videoTotal;

    [ObservableProperty]
    private bool _isDeleteInProgress;

    [ObservableProperty]
    private bool _isDeleteResultVisible;

    [ObservableProperty]
    private string _deleteProgressText = string.Empty;

    [ObservableProperty]
    private double _deleteProgressValue;

    [ObservableProperty]
    private double _deleteProgressMaximum = 1;

    [ObservableProperty]
    private string _deleteResultText = string.Empty;

    public GalleryViewModel(
        IMediaItemRepository mediaItems,
        IThumbnailService thumbnails,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(thumbnails);

        _mediaItems = mediaItems;
        _thumbnails = thumbnails;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>条目集合，供界面做增量虚拟化展示。</summary>
    public ObservableCollection<MediaItemViewModel> Items { get; } = [];

    /// <summary>删除通知条整体可见性：删除进行中或有待查看的结果时显示。</summary>
    public bool IsDeleteNotificationVisible => IsDeleteInProgress || IsDeleteResultVisible;

    partial void OnIsDeleteInProgressChanged(bool value) =>
        OnPropertyChanged(nameof(IsDeleteNotificationVisible));

    partial void OnIsDeleteResultVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsDeleteNotificationVisible));

    /// <summary>当前缩略图边长（像素）。</summary>
    public int ThumbnailSize => _thumbnailSize;

    /// <summary>当前条目总数。</summary>
    public int ItemCount => Items.Count;

    /// <summary>页头标题：随导航目标（图库 / 视频 / 收藏夹）变化；按文件夹或分类过滤时显示其名称。</summary>
    public string PageTitle =>
        _directoryName
        ?? _categoryName
        ?? (_onlyFavorites ? "收藏夹" : _kindFilter == MediaKind.Video ? "视频" : "图库");

    /// <summary>当前生效的类型筛选，供页头图标与筛选菜单勾选取用。</summary>
    public MediaKind? KindFilter => _kindFilter;

    /// <summary>页头图标，与左侧导航同形；文件夹与分类过滤均归入文件夹图形。</summary>
    public Symbol PageIcon => _directoryPath is not null || _categoryFilter.HasValue
        ? Symbol.Folder
        : _onlyFavorites ? Symbol.Favorite : _kindFilter == MediaKind.Video ? Symbol.Video : Symbol.Pictures;

    /// <summary>页头图标的字形码，与 PageIcon 同源，避免两处各自判定。</summary>
    /// <remarks>
    /// SymbolIcon 只暴露 Symbol、无法设置字号，与标题文字对齐须改用 FontIcon，
    /// 而 FontIcon 的 Glyph 是字符串。Symbol 枚举值即 Unicode 码点，直接强转即可。
    /// </remarks>
    public string PageIconGlyph => ((char)PageIcon).ToString();

    /// <summary>页头统计文本，反映当前筛选结果而非全库。</summary>
    /// <remarks>某一类为 0 时整项不显示，避免「123 张照片，0 个视频」这类无信息量的零值。</remarks>
    public string StatisticsText
    {
        get
        {
            if (PhotoTotal == 0 && VideoTotal == 0)
            {
                return "媒体库为空";
            }

            if (VideoTotal == 0)
            {
                return $"{PhotoTotal} 张照片";
            }

            if (PhotoTotal == 0)
            {
                return $"{VideoTotal} 个视频";
            }

            return $"{PhotoTotal} 张照片，{VideoTotal} 个视频";
        }
    }

    /// <summary>当前生效的筛选条件（不含分页），供列表查询与页头统计共用，避免两处条件漂移。</summary>
    private MediaQuery CurrentQuery => new()
    {
        Kind = _kindFilter,
        IsFavorite = _onlyFavorites ? true : null,
        CategoryId = _categoryFilter,
        DirectoryPath = _directoryPath,
        SortKey = _sortKey,
        SortDirection = _sortDirection,
        RandomSeed = _randomSeed,
        SearchText = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText
    };

    /// <summary>应用媒体类型筛选并重新加载。</summary>
    /// <param name="kind">媒体类型；为 null 表示全部。</param>
    public Task ApplyKindFilterAsync(MediaKind? kind)
    {
        _kindFilter = kind;
        OnPropertyChanged(nameof(PageTitle));
        return ReloadAsync();
    }

    /// <summary>应用「仅收藏」筛选并重新加载。</summary>
    /// <param name="onlyFavorites">是否仅显示收藏条目。</param>
    public Task ApplyFavoritesOnlyAsync(bool onlyFavorites)
    {
        _onlyFavorites = onlyFavorites;
        OnPropertyChanged(nameof(PageTitle));
        return ReloadAsync();
    }

    /// <summary>一次性应用导航目标对应的类型与收藏筛选，避免两个维度分别触发两次查询；同时重置文件夹与分类过滤，供根视图使用。</summary>
    /// <param name="kind">媒体类型；为 null 表示全部。</param>
    /// <param name="onlyFavorites">是否仅显示收藏条目。</param>
    public Task ApplyNavigationFilterAsync(MediaKind? kind, bool onlyFavorites)
    {
        _kindFilter = kind;
        _onlyFavorites = onlyFavorites;
        _categoryFilter = null;
        _categoryName = null;
        _directoryPath = null;
        _directoryName = null;
        NotifyFilterChanged();
        return ReloadAsync();
    }

    /// <summary>应用媒体文件夹过滤并重新加载：显示该文件夹及其子目录下的全部媒体。</summary>
    /// <param name="path">文件夹完整路径。</param>
    /// <param name="displayName">文件夹显示名，用于页头标题。</param>
    public Task ApplyMediaFolderFilterAsync(string path, string displayName)
    {
        _kindFilter = null;
        _onlyFavorites = false;
        _categoryFilter = null;
        _categoryName = null;
        _directoryPath = path;
        _directoryName = displayName;
        NotifyFilterChanged();
        return ReloadAsync();
    }

    /// <summary>应用分类过滤并重新加载：显示已归入该分类的媒体。</summary>
    /// <param name="categoryId">分类主键。</param>
    /// <param name="categoryName">分类名称，用于页头标题。</param>
    public Task ApplyCategoryFilterAsync(long categoryId, string categoryName)
    {
        _kindFilter = null;
        _onlyFavorites = false;
        _categoryFilter = categoryId;
        _categoryName = categoryName;
        _directoryPath = null;
        _directoryName = null;
        NotifyFilterChanged();
        return ReloadAsync();
    }

    /// <summary>页头标题与图标随过滤维度变化，须一并通知刷新。</summary>
    private void NotifyFilterChanged()
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageIcon));
        OnPropertyChanged(nameof(PageIconGlyph));
    }

    /// <summary>应用排序方式并重新加载。</summary>
    /// <param name="sortKey">排序依据。</param>
    /// <param name="sortDirection">排序方向；sortKey 为 Random 时被忽略。</param>
    public Task ApplySortOrderAsync(MediaSortKey sortKey, SortDirection sortDirection)
    {
        _sortKey = sortKey;
        _sortDirection = sortDirection;

        // 每次切到随机都换一个种子，用户得以反复重排；同一次随机浏览中种子不变，
        // 否则增量分页会拿到与已加载页重复或错位的条目。
        _randomSeed = sortKey == MediaSortKey.Random ? Random.Shared.Next(1, 1000000) : 0;

        return ReloadAsync();
    }

    /// <summary>应用搜索关键词并重新加载。</summary>
    /// <param name="searchText">关键词。</param>
    public Task ApplySearchAsync(string searchText)
    {
        _searchText = searchText ?? string.Empty;
        return ReloadAsync();
    }

    /// <summary>设置缩略图尺寸，并重新加载已有条目的缩略图。</summary>
    /// <param name="size">边长（像素）。</param>
    public async Task SetThumbnailSizeAsync(int size)
    {
        if (_thumbnailSize == size)
        {
            return;
        }

        _thumbnailSize = size;
        OnPropertyChanged(nameof(ThumbnailSize));

        List<MediaItemViewModel> pending = [];

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in Items)
            {
                item.Thumbnail = null;
            }

            pending = [.. Items];
        });

        await LoadThumbnailsForVisibleItemsAsync(pending);
    }

    /// <summary>丢弃已加载的缩略图并重新加载，用于显示缩放比变化后按新的物理像素重新解码。</summary>
    public async Task RefreshThumbnailsAsync()
    {
        List<MediaItemViewModel> pending = [];

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            pending = Items.Where(i => i.Thumbnail is not null).ToList();

            foreach (var item in pending)
            {
                item.Thumbnail = null;
            }
        });

        await LoadThumbnailsForVisibleItemsAsync(pending);
    }

    /// <summary>重新加载第一页数据。</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        await ExecuteLoadAsync(reset: true);
    }

    /// <summary>加载下一页数据。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync()
    {
        await ExecuteLoadAsync(reset: false);
    }

    /// <summary>切换收藏状态。</summary>
    /// <param name="item">目标条目。</param>
    [RelayCommand]
    public async Task ToggleFavoriteAsync(MediaItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var target = !item.Item.IsFavorite;
        var id = item.Id;

        // SetFavoriteAsync 内部使用 ConfigureAwait(false)，集合修改须切回 UI 线程。
        await _mediaItems.SetFavoriteAsync([id], target);

        MediaItemViewModel? updated = null;

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            var index = Items.IndexOf(item);

            if (index < 0)
            {
                return;
            }

            updated = new MediaItemViewModel(
                item.Item with { IsFavorite = target },
                _thumbnails.LoadThumbnailAsyncCore);

            Items[index] = updated;
        });

        // 新实例没有宽高比，不预取会让该条目在收藏切换时跳回方图再跳回来。
        // 不能在上面的 UI 线程块内 await：预取内部还要排队回 UI 线程，会自我死锁。
        if (updated is not null)
        {
            await PrefetchDimensionsAsync([updated]);
        }
    }

    /// <summary>批量收藏当前选中的条目。</summary>
    /// <param name="items">选中条目。</param>
    public async Task SetFavoriteForSelectionAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(i => i.Id).ToList();
        await _mediaItems.SetFavoriteAsync(ids, true);
        await ReloadAsync();
    }

    /// <summary>把指定条目对应的磁盘文件逐个移入回收站，原地从列表移除，并经通知条展示进度与结果。</summary>
    /// <param name="items">待删除条目。</param>
    /// <returns>(成功删除数, 失败数)。</returns>
    /// <remarks>
    /// 回收站删除（RecycleBinHelper，SHFileOperation + FOF_ALLOWUNDO）同步且耗时，放线程池执行避免卡 UI；
    /// 每删一项即回 UI 线程从集合移除并推进进度，后续条目自然前移补位，不整页重载；
    /// 结束后按成功路径批量清索引并刷新页头统计；可经 CancelDelete 中止，已删部分保留。
    /// </remarks>
    public async Task<(int Deleted, int Failed)> DeleteFilesAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0 || IsDeleteInProgress)
        {
            return (0, 0);
        }

        CloseDeleteResult();

        _deleteCts = new CancellationTokenSource();
        var token = _deleteCts.Token;

        var targets = items.ToList();
        var deletedPaths = new List<string>(targets.Count);
        var deleted = 0;
        var failed = 0;
        string? firstError = null;
        var cancelled = false;

        IsDeleteInProgress = true;
        DeleteProgressMaximum = targets.Count;
        DeleteProgressValue = 0;
        DeleteProgressText = BuildProgressText(0, targets.Count);

        foreach (var item in targets)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var ok = false;

            try
            {
                // 同步的 SHFileOperation 放线程池，避免批量删除期间冻结界面。
                ok = await Task.Run(() => RecycleBinHelper.SendToRecycleBin(item.Item.Path), token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }

            if (ok)
            {
                deleted++;
                deletedPaths.Add(item.Item.Path);

                // 先取消在途解码再移除：条目一旦离开集合，解码任务无从取消，会白占信号量槽位。
                // 绑定属性的赋值统一入 UI 队列：EnqueueAsync 的延续落在线程池线程（见 ExecuteLoadAsync）。
                await _dispatcherQueue.EnqueueAsync(() =>
                {
                    item.CancelPendingLoad();
                    Items.Remove(item);
                    OnPropertyChanged(nameof(ItemCount));
                    DeleteProgressValue = deleted;
                    DeleteProgressText = BuildProgressText(deleted, targets.Count);
                });
            }
            else
            {
                failed++;
                firstError ??= $"无法将文件移入回收站：{item.Item.Path}";
            }
        }

        // 只要有一个成功，索引就按实际路径清理，避免残留幽灵条目。
        if (deletedPaths.Count > 0)
        {
            await _mediaItems.DeleteByPathsAsync(deletedPaths);
        }

        if (SelectedItem is not null && deletedPaths.Contains(SelectedItem.Item.Path))
        {
            SelectedItem = null;
        }

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            IsDeleteInProgress = false;
            ShowDeleteResult(BuildDeleteResultText(cancelled, deleted, failed, firstError));
        });

        _deleteCts.Dispose();
        _deleteCts = null;

        // 页头统计反映的是筛选结果全量规模，删除后须同步收缩，但不重载列表本身。
        if (deletedPaths.Count > 0)
        {
            await RefreshStatisticsAsync();
        }

        return (deleted, failed);
    }

    /// <summary>请求中止正在进行的删除；已移入回收站的部分保留。</summary>
    public void CancelDelete() => _deleteCts?.Cancel();

    /// <inheritdoc />
    /// <remarks>仅释放删除用取消令牌；删除正常结束时已就地释放并置空，此处兜底应用退出场景。</remarks>
    public void Dispose()
    {
        _deleteCts?.Dispose();
        _deleteCts = null;
    }

    /// <summary>关闭删除结果通知条（手动关闭与自动消失定时器共用）。</summary>
    public void CloseDeleteResult()
    {
        _deleteResultTimer?.Stop();
        IsDeleteResultVisible = false;
    }

    /// <summary>删除进度通知文本。</summary>
    private string BuildProgressText(int done, int total) =>
        $"正在从「{PageTitle}」中删除 {done}/{total} 项。";

    /// <summary>删除结果通知文本：取消 / 全部成功 / 部分失败 / 全部失败四种形态。</summary>
    private string BuildDeleteResultText(bool cancelled, int deleted, int failed, string? firstError)
    {
        if (cancelled)
        {
            return deleted == 0 ? "已取消删除。" : $"已删除 {deleted} 项，已取消。";
        }

        if (failed == 0)
        {
            return $"一切就绪！已成功从「{PageTitle}」中删除 {deleted} 项。";
        }

        if (deleted == 0)
        {
            return $"删除失败：{firstError}";
        }

        return $"已删除 {deleted} 项，{failed} 项无法删除。";
    }

    /// <summary>显示删除结果通知条，5 秒后自动消失。</summary>
    private void ShowDeleteResult(string text)
    {
        DeleteResultText = text;
        IsDeleteResultVisible = true;

        // 定时器须在 UI 线程创建，懒初始化后复用；每次显示前重置，避免上次的 Tick 提前关闭本次结果。
        _deleteResultTimer ??= _dispatcherQueue.CreateTimer();
        _deleteResultTimer.Stop();
        _deleteResultTimer.Interval = DeleteResultAutoCloseDelay;
        _deleteResultTimer.Tick -= OnDeleteResultTimerTick;
        _deleteResultTimer.Tick += OnDeleteResultTimerTick;
        _deleteResultTimer.Start();
    }

    private void OnDeleteResultTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        IsDeleteResultVisible = false;
    }

    /// <summary>重命名磁盘文件并同步更新索引中的路径信息。</summary>
    /// <param name="item">待重命名条目。</param>
    /// <param name="newName">不含路径的新文件名。</param>
    /// <returns>错误信息；成功时为 null。</returns>
    public async Task<string?> RenameAsync(MediaItemViewModel item, string newName)
    {
        var oldPath = item.Item.Path;
        var directory = Path.GetDirectoryName(oldPath) ?? string.Empty;
        var newPath = Path.Combine(directory, newName);

        if (File.Exists(newPath))
        {
            return $"目标已存在同名文件：{newPath}";
        }

        try
        {
            File.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return ex.Message;
        }

        var source = item.Item;
        var updated = source with
        {
            Path = newPath,
            FileName = newName,
            Directory = directory,
        };

        await _mediaItems.UpsertBatchAsync(new[] { updated });

        if (SelectedItem == item)
        {
            SelectedItem = null;
        }

        await ReloadAsync();
        return null;
    }

    private bool CanLoadMore() => HasMore && !IsLoading;

    private async Task ExecuteLoadAsync(bool reset)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        LoadMoreCommand.NotifyCanExecuteChanged();
        StatusText = "正在加载…";

        try
        {
            // Skip 用与集合数量解耦的游标：删除操作会原地收缩 Items，若以 Items.Count 为偏移，
            // 下一页会与数据库错位、把已展示的条目重复拉取一遍。
            var query = CurrentQuery with
            {
                Skip = reset ? 0 : _loadedCount,
                Take = PageSize
            };

            // QueryAsync 内部使用 ConfigureAwait(false)，await 之后当前线程已是线程池线程。
            // ObservableCollection 与 BitmapImage 只能在 UI 线程操作，故必须切回 UI 线程。
            var page = await _mediaItems.QueryAsync(query);

            List<MediaItemViewModel> pending = [];

            await _dispatcherQueue.EnqueueAsync(() =>
            {
                if (reset)
                {
                    foreach (var stale in Items)
                    {
                        stale.CancelPendingLoad();
                    }

                    Items.Clear();
                    _loadedCount = 0;
                }

                foreach (var item in page)
                {
                    Items.Add(new MediaItemViewModel(item, _thumbnails.LoadThumbnailAsyncCore));
                }

                _loadedCount += page.Count;
                HasMore = page.Count == PageSize;
                StatusText = $"共 {Items.Count} 项";
                OnPropertyChanged(nameof(ItemCount));

                pending = Items.Where(i => i.Thumbnail is null).ToList();
            });

            if (reset)
            {
                await RefreshStatisticsAsync();
            }

            // 先定宽高比再加载缩略图：位图到位时宽高比若已与预取值一致就不会重排，
            // 否则每个条目都要先从方图跳到真实比例，整行跟着抖。
            await PrefetchDimensionsAsync(pending);

            // 提交本页未加载条目解码。两个视图的 GridView 虽启用 UI 虚拟化，但实测
            // ContainerContentChanging 在首屏 / 重解码场景下不足以覆盖全部条目，整页提交是
            // 缩略图可见性的兜底。滚动停止时由页面 CancelOffscreenThumbnails 取消已滚出视口的
            // 在途项，把信号量槽位让给新进入视口的条目，避免不可见项占满队列导致尾延迟雪崩。
            await LoadThumbnailsForVisibleItemsAsync(pending);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            await _dispatcherQueue.EnqueueAsync(() => StatusText = "加载失败，请重试");
        }
        finally
        {
            // await EnqueueAsync 的延续会落回线程池线程，IsLoading 触发的 PropertyChanged
            // 必须回到 UI 线程，故这里也要经过调度。
            await _dispatcherQueue.EnqueueAsync(() =>
            {
                IsLoading = false;
                LoadMoreCommand.NotifyCanExecuteChanged();
            });
        }
    }

    /// <summary>并发预取条目尺寸，使布局在缩略图解码完成前就按真实宽高比排列。</summary>
    /// <param name="items">待预取的条目。</param>
    private async Task PrefetchDimensionsAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        var results = new ConcurrentBag<(MediaItemViewModel Item, int Width, int Height)>();

        // 探测与属性读取都不触碰 DependencyObject，可在线程池并行；只写回 UI 线程。
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = DimensionPrefetchConcurrency },
            async (item, token) =>
            {
                var size = await _thumbnails.GetDimensionsAsync(item.Item.Path, token);

                if (size is not null)
                {
                    results.Add((item, size.Value.Width, size.Value.Height));
                }
            });

        if (results.IsEmpty)
        {
            return;
        }

        // 一次性写回：AspectRatio 变更会触发布局面板重测，逐条 await 会让 UI 线程切换成为瓶颈。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var (item, width, height) in results)
            {
                item.SetDimensions(width, height);
            }
        });
    }

    /// <summary>为指定条目加载缩略图，并等待全部完成。</summary>
    /// <param name="pending">待加载缩略图的条目。</param>
    /// <remarks>
    /// 只入队一次 UI 线程：逐条 EnqueueAsync 会让每个条目多付一次线程切换，
    /// 上千条时调度开销本身就成为瓶颈。实际的解码并发由缩略图服务的信号量统一限流，
    /// 此处不再自行节流，否则两级限流会互相掩盖真实并发度。
    /// </remarks>
    private async Task LoadThumbnailsForVisibleItemsAsync(IReadOnlyList<MediaItemViewModel> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(pending.Count);

        // EnsureThumbnailAsync 内部会创建 BitmapImage（DependencyObject，具线程亲和性），
        // 必须在 UI 线程发起；此处只收集任务，不能在 lambda 内 await，否则会自我死锁。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in pending)
            {
                tasks.Add(item.EnsureThumbnailAsync(_thumbnailSize));
            }
        });

        // 必须等待而非 fire-and-forget：不等待会让上一页的解码任务与下一页请求叠加，
        // 队列越滚越长，表现为缩略图延迟随滚动距离线性恶化。
        await Task.WhenAll(tasks);
    }

    /// <summary>刷新页头统计：反映当前筛选结果（类型 / 收藏 / 搜索），而非全库。</summary>
    /// <remarks>已指定 kind 时另一侧必然为 0，直接短路，省掉一次 COUNT。</remarks>
    private async Task RefreshStatisticsAsync()
    {
        var photoCount = _kindFilter is MediaKind.Video
            ? 0
            : await _mediaItems.CountByQueryAsync(CurrentQuery with { Kind = MediaKind.Image });

        var videoCount = _kindFilter is MediaKind.Image
            ? 0
            : await _mediaItems.CountByQueryAsync(CurrentQuery with { Kind = MediaKind.Video });

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            PhotoTotal = photoCount;
            VideoTotal = videoCount;
            OnPropertyChanged(nameof(StatisticsText));
        });
    }
}
