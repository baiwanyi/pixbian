/**
 * 图库页视图模型（M2）。
 * 职责：按条件分页加载媒体条目，管理多选、批量收藏、移除索引，并维护日期分组、全库统计与排序筛选。
 * 复用约定：数据访问全部经 IMediaItemRepository，禁止在此编写 SQL 或直接触碰文件系统；
 *          全部命令基于 CommunityToolkit.Mvvm 的 AsyncRelayCommand，自动维护 CanExecute 与并发保护。
 * 关键约束：分页为追加模式，切换筛选或搜索时必须先清空集合并把 Skip 归零，否则会串页；
 *          日期分组依赖排序的全局有序性（同排序下同日期条目相邻），分组采用「相邻同日期合并」策略；
 *          批量删除只移除索引记录，不动磁盘文件；缩略图加载失败不得中断列表渲染。
 */

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>图库页视图模型。</summary>
public sealed partial class GalleryViewModel : ObservableObject
{
    private const int PageSize = 200;

    /// <summary>尺寸预取的并发度：只读文件头，并发远快于串行，但过高会与缩略图解码争抢 IO。</summary>
    private const int DimensionPrefetchConcurrency = 4;

    private readonly IMediaItemRepository _mediaItems;
    private readonly IThumbnailService _thumbnails;
    private readonly DispatcherQueue _dispatcherQueue;

    private MediaKind? _kindFilter;
    private bool _onlyFavorites;
    private MediaSortOrder _sortOrder = MediaSortOrder.TakenDescending;
    private string? _lastGroupKey;
    private string _searchText = string.Empty;
    private int _thumbnailSize = ThumbnailSizes.Default;

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

    /// <summary>按拍摄日期分组的条目集合，供分组网格展示。</summary>
    public ObservableCollection<MediaGroupViewModel> Groups { get; } = [];

    /// <summary>当前缩略图边长（像素）。</summary>
    public int ThumbnailSize => _thumbnailSize;

    /// <summary>当前条目总数。</summary>
    public int ItemCount => Items.Count;

    /// <summary>页头标题：随导航目标（图库 / 视频 / 收藏夹）变化。</summary>
    public string PageTitle => _onlyFavorites ? "收藏夹" : _kindFilter == MediaKind.Video ? "视频" : "图库";

    /// <summary>页头统计文本（如「123 张照片，5 个视频」），反映全库而非当前筛选。</summary>
    public string StatisticsText => PhotoTotal == 0 && VideoTotal == 0
        ? "媒体库为空"
        : $"{PhotoTotal} 张照片，{VideoTotal} 个视频";

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

    /// <summary>一次性应用导航目标对应的类型与收藏筛选，避免两个维度分别触发两次查询。</summary>
    /// <param name="kind">媒体类型；为 null 表示全部。</param>
    /// <param name="onlyFavorites">是否仅显示收藏条目。</param>
    public Task ApplyNavigationFilterAsync(MediaKind? kind, bool onlyFavorites)
    {
        _kindFilter = kind;
        _onlyFavorites = onlyFavorites;
        OnPropertyChanged(nameof(PageTitle));
        return ReloadAsync();
    }

    /// <summary>应用排序方式并重新加载。</summary>
    /// <param name="sortOrder">排序方式。</param>
    public Task ApplySortOrderAsync(MediaSortOrder sortOrder)
    {
        _sortOrder = sortOrder;
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

            // 分组集合持有同一实例，必须同步替换，否则收藏角标两处显示不一致。
            foreach (var group in Groups)
            {
                var groupIndex = group.Items.IndexOf(item);

                if (groupIndex >= 0)
                {
                    group.Items[groupIndex] = updated;
                    break;
                }
            }
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

    /// <summary>从索引中移除指定条目（不删除磁盘文件）。</summary>
    /// <param name="items">待移除条目。</param>
    public async Task RemoveFromIndexAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(i => i.Id).ToList();
        await _mediaItems.DeleteByIdsAsync(ids);
        await ReloadAsync();
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
            var query = new MediaQuery
            {
                Kind = _kindFilter,
                IsFavorite = _onlyFavorites ? true : null,
                SortOrder = _sortOrder,
                SearchText = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
                Skip = reset ? 0 : Items.Count,
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
                    Groups.Clear();
                    _lastGroupKey = null;
                }

                foreach (var item in page)
                {
                    var viewModel = new MediaItemViewModel(item, _thumbnails.LoadThumbnailAsyncCore);
                    Items.Add(viewModel);
                    AddToGroups(viewModel);
                }

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

    /// <summary>把条目归入日期分组；排序的全局有序性保证同日期条目相邻，故采用相邻合并策略。</summary>
    /// <param name="item">待归组条目。</param>
    private void AddToGroups(MediaItemViewModel item)
    {
        var key = item.TakenDateText;

        if (key != _lastGroupKey)
        {
            Groups.Add(new MediaGroupViewModel(key));
            _lastGroupKey = key;
        }

        Groups[^1].Items.Add(item);
    }

    /// <summary>刷新全库照片 / 视频统计，供页头展示（不受当前筛选影响）。</summary>
    private async Task RefreshStatisticsAsync()
    {
        var photoCount = await _mediaItems.CountAsync(MediaKind.Image);
        var videoCount = await _mediaItems.CountAsync(MediaKind.Video);

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            PhotoTotal = photoCount;
            VideoTotal = videoCount;
            OnPropertyChanged(nameof(StatisticsText));
        });
    }
}
