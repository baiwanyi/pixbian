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
using Microsoft.UI.Xaml.Media;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>图库页视图模型。</summary>
public sealed partial class GalleryViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 200;

    /// <summary>首屏优先提交的条目数：虚拟化下可见约 40 条，取 1.5 倍余量兼顾滚动衔接。
    /// 撤层（覆盖层消失）只等这批完成，积压批在后台继续渐进。</summary>
    private const int FirstScreenSubmitCount = 60;

    /// <summary>缩略图提交批的批次大小：位图创建与视觉状态切换都在 UI 线程，批次越小
    /// 单次回调洪峰越短，批间让出后 UI 保持可交互（点击 / 滚动可随时插队）。</summary>
    private const int ThumbnailBatchSize = 10;

    /// <summary>提交批之间的让出间隔：给输入与渲染留执行窗。</summary>
    private static readonly TimeSpan ThumbnailBatchGap = TimeSpan.FromMilliseconds(40);

    /// <summary>切换视图触发后台强制 GC 的已释放条目数阈值：低于该值交由常规 GC 自然回收，
    /// 避免为几百 KB 的回收付出全堆标记的停顿。</summary>
    private const int AggressiveGcItemThreshold = 300;

    /// <summary>随机序值的取模上界，须与 Schema v4 触发器/回填的表达式严格一致。</summary>
    private const long RandomRankModulus = 2147483647;

    /// <summary>整页缩略图解码的等待上限。单条编码已在服务层限时，此上限兜底「状态机
    /// 不被解码拖死」：超时后加载流程照常收口（LOADTOTAL/撤 loading），未完成的解码
    /// 在后台继续，位图就绪后经属性通知自然渐入，无需重试机制。</summary>
    private static readonly TimeSpan ThumbnailWaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>尺寸预取的并发度：只读文件头，并发远快于串行，但过高会与缩略图解码争抢 IO。</summary>
    private const int DimensionPrefetchConcurrency = 4;

    private readonly IMediaItemRepository _mediaItems;
    private readonly IThumbnailService _thumbnails;
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>缩略图解码调度器：视口窗口驱动提交，解码量与集合规模解耦（P1b）。</summary>
    private readonly ThumbnailLoadScheduler _scheduler;

    private MediaKind? _kindFilter;
    private bool _onlyFavorites;
    private long? _categoryFilter;
    private string? _categoryName;
    private string? _directoryPath;
    private string? _directoryName;
    private MediaSortKey _sortKey = MediaSortKey.ModifiedDate;
    private SortDirection _sortDirection = SortDirection.Descending;
    private int _randomSeed;

    /// <summary>随机浏览的游标（random_rank 起点，含端点）；视图重置（洗牌）时生成，翻页时推进。
    /// 取值域与 Schema v4 的 rank 一致：[0, RandomRankModulus - 1]。</summary>
    private long? _randomCursor;
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

    /// <summary>内容区查询就绪状态：页面初始化即为 true（先 loading 后出内容），首次加载撤除。</summary>
    [ObservableProperty]
    private bool _isQuerying = true;

    /// <summary>最近一次加载是否失败；空状态据此显示失败提示而非「没有照片或视频」。
    /// 每次发起新查询时复位，成功完成时再次复位。</summary>
    [ObservableProperty]
    private bool _isLoadFailed;

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
        _scheduler = new ThumbnailLoadScheduler(() => Items, () => _thumbnailSize, _dispatcherQueue);
        _thumbnails.ThumbnailEvicted += OnThumbnailEvicted;
    }

    /// <summary>内存缓存容量淘汰回调（线程池触发）：回 UI 线程置空对应条目，交还调度器按视口恢复。</summary>
    /// <remarks>被淘汰条目与「从未加载」在调度器收编逻辑中同一处理（Thumbnail 为 null 即收编），
    /// 无需独立登记集合；滚动过期与显式移除不触发本事件，显示中的条目不受影响。</remarks>
    private void OnThumbnailEvicted(string path)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var item = Items.FirstOrDefault(i => i.Item.Path == path);

            if (item is not null)
            {
                item.Thumbnail = null;
            }
        });
    }

    private ObservableCollection<MediaItemViewModel> _items = [];

    /// <summary>条目集合，供界面做增量虚拟化展示。</summary>
    /// <remarks>
    /// 重置（切换文件夹/导航）时整体替换实例而非原地清空再逐条添加：新集合尚无绑定订阅者，
    /// 填充不触发任何集合通知，随后一次属性通知完成 ItemsSource 整体替换，界面只重建一轮。
    /// 原地逐条添加会为每条付一次双视图集合通知，切换文件夹时的 UI 卡顿主要来自这里。
    /// </remarks>
    public ObservableCollection<MediaItemViewModel> Items => _items;

    /// <summary>整体替换条目集合并补发 ItemCount 通知：集合实例替换不会触发 CollectionChanged，
    /// 漏发会让页面空状态停留在旧值（GalleryPage.ShowEmptyState 的数据源）。</summary>
    private void ReplaceItemsCore(ObservableCollection<MediaItemViewModel> fresh)
    {
        _items = fresh;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(ItemCount));
    }

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

    /// <summary>页头图标的字形码，与左侧导航同形；文件夹与分类过滤归入空心文件夹图形。</summary>
    /// <remarks>
    /// SymbolIcon 只暴露 Symbol、无法设置字号，与标题文字对齐须改用 FontIcon，
    /// 而 FontIcon 的 Glyph 是字符串。Symbol 枚举值即 Unicode 码点，直接强转即可。
    /// 空心文件夹用 \uED25：Segoe Fluent Icons 的 E8B7（Symbol.Folder）是实心 FolderFill，
    /// E8B7 在 MDL2 中又是另一图形（文件+书签），ED25 在两代字体下均为空心斜开盖文件夹。
    /// 收藏夹用 \uEB51（空心爱心，用户指定）；EB52 为实心爱心（Symbol.Favorite），E734 为线性星形。
    /// </remarks>
    public string PageIconGlyph => _directoryPath is not null || _categoryFilter.HasValue
        ? "\uED25"
        : _onlyFavorites ? "\uEB51"
        : ((char)(_kindFilter == MediaKind.Video ? Symbol.Video : Symbol.Pictures)).ToString();

    /// <summary>页头统计文本，反映当前筛选结果而非全库。</summary>
    /// <remarks>某一类为 0 时整项不显示，避免「123 张照片，0 个视频」这类无信息量的零值。</remarks>
    public string StatisticsText
    {
        get
        {
            if (PhotoTotal == 0 && VideoTotal == 0)
            {
                return "0 项";
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

    /// <summary>设置缩略图尺寸，并按视口窗口重新加载条目缩略图。</summary>
    /// <param name="size">边长（像素）。</param>
    public async Task SetThumbnailSizeAsync(int size)
    {
        if (_thumbnailSize == size)
        {
            return;
        }

        _thumbnailSize = size;
        OnPropertyChanged(nameof(ThumbnailSize));

        // 档位切换改变解码桶，整批条目需重解；只解视口窗口，其余滚动到时恢复（虚拟化常态）。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in Items)
            {
                item.Thumbnail = null;
            }
        });

        _scheduler.RefreshViewport();
    }

    /// <summary>丢弃已加载的缩略图并按视口窗口重新加载，用于显示缩放比变化后按新的物理像素重新解码。</summary>
    public async Task RefreshThumbnailsAsync()
    {
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in Items)
            {
                item.Thumbnail = null;
            }
        });

        _scheduler.RefreshViewport();
    }

    /// <summary>视口区间更新（页面滚动停止时转发调度器）：收编窗口内待解条目并按优先级渐进提交。</summary>
    /// <param name="firstVisible">可见区间首个索引（含）。</param>
    /// <param name="lastVisible">可见区间末个索引（含）。</param>
    public void UpdateViewport(int firstVisible, int lastVisible) =>
        _scheduler.UpdateViewport(firstVisible, lastVisible);

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

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            var index = Items.IndexOf(item);

            if (index < 0)
            {
                return;
            }

            // 收藏夹视图下取消收藏：条目已不符合「收藏夹内容」的列表语义，原地移除而非保留显示。
            if (_onlyFavorites && !target)
            {
                Items.RemoveAt(index);
                return;
            }

            // 原位更新而非替换条目：保留已解码缩略图与显示容器，避免条目闪回骨架屏，
            // 也让条目上正在播放的收藏动画不被元素重建打断。
            item.SetFavorite(target);
        });
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

        IsDeleteInProgress = true;
        DeleteProgressMaximum = targets.Count;
        DeleteProgressValue = 0;
        DeleteProgressText = BuildProgressText(0, targets.Count);

        // 全程 try/finally：置位与复位之间任何一环抛异常（回收站 Win32 失败、
        // 数据库写入失败）都必须复位 IsDeleteInProgress——否则该标志永久为真，
        // 而本方法开头的守卫会让此后**所有**删除静默失效（实测即此症状）。
        (int Deleted, int Failed, bool Cancelled, string? FirstError) result = (0, 0, false, null);
        string? interruptError = null;

        try
        {
            result = await RunDeleteLoopAsync(targets, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 非取消异常收口到通知条：既不吞掉信息，也不让界面毫无反馈。
            interruptError = $"{ex.GetType().Name}：{ex.Message}";
        }
        finally
        {
            IsDeleteInProgress = false;
            _deleteCts?.Dispose();
            _deleteCts = null;
        }

        var text = interruptError is null
            ? BuildDeleteResultText(result.Cancelled, result.Deleted, result.Failed, result.FirstError)
            : $"删除中断：{interruptError}";

        await _dispatcherQueue.EnqueueAsync(() => ShowDeleteResult(text));

        return (result.Deleted, result.Failed);
    }

    /// <summary>逐个把条目移入回收站并同步集合与索引。</summary>
    private async Task<(int Deleted, int Failed, bool Cancelled, string? FirstError)> RunDeleteLoopAsync(
        IReadOnlyList<MediaItemViewModel> targets,
        CancellationToken token)
    {
        var deletedPaths = new List<string>(targets.Count);
        var deleted = 0;
        var failed = 0;
        var cancelled = false;
        string? firstError = null;

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
                    _scheduler.Remove(item);
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
            // 索引清理不接受取消令牌：文件已移入回收站，此刻中断会留下指向已删文件的
            // 幽灵条目，后续浏览与统计都会错——宁可多花一次写库也必须完成。
            await _mediaItems.DeleteByPathsAsync(deletedPaths, CancellationToken.None);

            if (SelectedItem is not null && deletedPaths.Contains(SelectedItem.Item.Path))
            {
                SelectedItem = null;
            }

            // 页头统计反映的是筛选结果全量规模，删除后须同步收缩，但不重载列表本身。
            await RefreshStatisticsAsync(_loadSequence);
        }

        return (deleted, failed, cancelled, firstError);
    }

    /// <summary>请求中止正在进行的删除；已移入回收站的部分保留。</summary>
    public void CancelDelete() => _deleteCts?.Cancel();

    /// <inheritdoc />
    /// <remarks>仅释放删除用取消令牌与调度器；删除正常结束时已就地释放并置空，此处兜底应用退出场景。</remarks>
    public void Dispose()
    {
        _thumbnails.ThumbnailEvicted -= OnThumbnailEvicted;
        _scheduler.Dispose();
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

    /// <summary>加载请求代数：新请求立即使旧请求过期，旧任务不得再写 UI 或收尾加载状态。</summary>
    private int _loadSequence;

    /// <summary>与代数配套的取消源：切走后立即终止旧请求的尺寸预取，不再继续灌文件 IO。</summary>
    private CancellationTokenSource? _loadCts;

    private async Task ExecuteLoadAsync(bool reset)
    {
        // 不以 IsLoading 提前返回：快速切换文件夹时旧加载往往仍在途（尺寸预取与缩略图解码耗时），
        // 吞掉新请求会表现为「点了没反应」的卡顿。改为最新请求胜出——旧任务在各阶段检查代数后放弃。
        var sequence = ++_loadSequence;

        // 立即终止旧请求的预取：旧全量文件 IO 若继续跑会与新请求线性叠加，多次快速切换后
        // IO 竞争拖垮预取与解码。只取消不释放，理由同 MediaItemViewModel 的 CTS 约定。
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var prefetchToken = _loadCts.Token;

        IsLoading = true;
        LoadMoreCommand.NotifyCanExecuteChanged();
        StatusText = "正在加载…";

        // 切换视图时立即点亮点击反馈之外的内容区 loading 覆盖层：此时仍在点击处理器同步段，
        // 通知先于后续任何重活到达界面；缩略图全部就绪才撤层会造成长时间转圈，
        // 故撤层时机定在宽高比写回（布局定型）之后，见下方 reset 分支。
        if (reset)
        {
            IsQuerying = true;
            IsLoadFailed = false;

            // 切换视图即彻底释放当前列表（用户方案）：位图被 ViewModel、条目模板与
            // 内存缓存三处引用，仅替换集合引用会让数百 MB 位图滞留为垃圾，等下一次
            // 分配风暴触发 GC 时以长暂停形式爆发（实测点「随机」即未响应）。
            // 此处同步解绑三处引用；触发底翻页后（规模超阈值）再主动压缩内存——
            // 切换是一次性的用户动作，数百毫秒的确定性停顿远好于随后的 GC 风暴。
            // 注意用 Release 而非 Invalidate：磁盘成品仍有效，删磁盘会让每次切换
            // 清空上一目录缓存，下一轮全部重新解码（实测即切即卡 + 未响应的元凶）。
            var staleCount = _items.Count;

            foreach (var stale in _items)
            {
                stale.CancelPendingLoad();
                _thumbnails.Release(stale.Item.Path);
                stale.Thumbnail = null;
            }

            if (staleCount > 0)
            {
                ReplaceItemsCore([]);
            }

            if (staleCount > AggressiveGcItemThreshold)
            {
                // 后台线程收集：blocking+compacting 在 GB 级堆上会令 UI 完全暂停数秒
                // （实测即未响应），移到后台后 UI 仅在标记阶段短暂参与。
                _ = Task.Run(() =>
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: false);
                });
            }

            // 随机浏览在此洗牌：每次切换视图都生成新的随机起点，翻页时游标随后推进。
            if (_sortKey == MediaSortKey.Random)
            {
                _randomCursor = Random.Shared.NextInt64(0, RandomRankModulus);
            }

            // SQLite 的 Async 方法多为同步完成的包装：直接继续时 await 不会让出 UI 线程，
            // 替换块会先于首帧渲染入队执行。曾以 CompositionTarget.Rendering 等待渲染帧错峰，
            // 但该订阅与渲染 tick 抢占执行窗，实测令合成呈现停摆（UI 线程存活、布局 pass
            // 永久停摆、画面冻结在最后一帧，三组减法实验实锤）——机制已永久移除。
            // 覆盖层通知在点击处理器同步段发出，本身早于任何重活到达界面，无需额外等待。
        }

        try
        {
            // Skip 用与集合数量解耦的游标：删除操作会原地收缩 Items，若以 Items.Count 为偏移，
            // 下一页会与数据库错位、把已展示的条目重复拉取一遍。
            var query = CurrentQuery with
            {
                Skip = reset ? 0 : _loadedCount,
                Take = PageSize,
                RandomCursor = _sortKey == MediaSortKey.Random ? _randomCursor : null
            };

            // QueryAsync 内部使用 ConfigureAwait(false)，await 之后当前线程已是线程池线程。
            // ObservableCollection 与 BitmapImage 只能在 UI 线程操作，故必须切回 UI 线程。
            var page = await _mediaItems.QueryAsync(query);

            List<MediaItemViewModel> pending = [];

            // 本页新建的条目：替代原「全集合扫描 Thumbnail is null」的提交来源（R1 修复）。
            var added = new List<MediaItemViewModel>(page.Count);

            // 期间又来了新请求：本次结果作废，不再触碰集合与加载状态。
            if (sequence != _loadSequence)
            {
                return;
            }

            // 集合替换是单次最大的 UI 块工作（数百容器同步生成），必须保持 Normal 优先级：
            // 撤层也在 Normal 队列且排在预取之后，FIFO 保证替换先于撤层执行——
            // 替换冻结全程被 loading 覆盖层遮蔽，撤层瞬间骨架屏已就位；若降为 Low，
            // 撤层会先执行，露出空白网格后再裸奔大冻结，表现为卡死。
            await _dispatcherQueue.EnqueueAsync(() =>
            {
                // 入队与执行之间可能已有更新请求：过期请求放弃整块 UI 重建。快速连续切换时
                // 若不做此检查，每次都要付一次「200 容器销毁 + 创建 + 全量重排」（实测
                // UI 线程 2~5 秒/次），十次切换即表现为未响应；只保留最新一次的重建。
                if (sequence != _loadSequence)
                {
                    return;
                }

                if (reset)
                {
                    foreach (var stale in _items)
                    {
                        stale.CancelPendingLoad();
                    }

                    _scheduler.Reset();

                    // 新集合尚无订阅者，逐条填充零通知成本；见 Items 属性注释。
                    var fresh = new ObservableCollection<MediaItemViewModel>();

                    foreach (var item in page)
                    {
                        var vm = new MediaItemViewModel(item, _thumbnails.LoadThumbnailAsyncCore);
                        fresh.Add(vm);
                        added.Add(vm);
                    }

                    ReplaceItemsCore(fresh);
                    _loadedCount = 0;
                }
                else
                {
                    foreach (var item in page)
                    {
                        var vm = new MediaItemViewModel(item, _thumbnails.LoadThumbnailAsyncCore);
                        _items.Add(vm);
                        added.Add(vm);
                    }

                    // 位图内存交由内存缓存的字节限额 LRU 统一管理（容量淘汰经事件回置条目），
                    // 不再按索引做头部瘦身——解码量已由调度器收敛到视口，无「释放→重解」自激。
                }

                _loadedCount += page.Count;
                HasMore = page.Count == PageSize;

                // 随机游标推进到本页末条之后：翻页自下一条继续，不重复不遗漏。
                // 扫到尾部（count < PageSize）时 HasMore 归假，用户再点「随机」即重新洗牌。
                if (_sortKey == MediaSortKey.Random
                    && page.Count > 0
                    && page[^1].RandomRank is { } lastRank)
                {
                    _randomCursor = lastRank + 1;
                }

                StatusText = $"共 {_items.Count} 项";
                OnPropertyChanged(nameof(ItemCount));

                // 只提交本页新增条目：全量扫描会把头部瘦身置空的历史条目重新提交解码，
                // 与瘦身叠加成「越翻页提交越多」的自激放大（实测第 5 页单次 906 条）。
                // 被瘦身条目的重解改由页面按视口窗口经 RestoreEvictedInWindowAsync 驱动。
                pending = added;
            });

            if (sequence != _loadSequence)
            {
                return;
            }

            if (reset)
            {
                // 统计不阻塞主加载链：COUNT 在后台并行推进，回写前校验代数，
                // 过期结果直接丢弃。页头数字允许比列表晚到位——撤层换来的首屏提前
                // 远比「数字晚几百毫秒」重要（A4：串行 STAT 曾占撤层前的全部等待）。
                _ = RefreshStatisticsAsync(sequence);
            }

            // 先定宽高比再加载缩略图：位图到位时宽高比若已与预取值一致就不会重排，
            // 否则每个条目都要先从方图跳到真实比例，整行跟着抖。
            // 只对索引里没有宽高的条目探测文件头：后台元数据回填完成之后这里为空集合，
            // 打开文件夹不再产生任何文件 IO，转圈时长只剩一次 SQL 查询与缩略图解码。
            var dimensionPending = pending.Where(i => i.NeedsDimensionProbe).ToList();

            await PrefetchDimensionsAsync(dimensionPending, sequence, prefetchToken);

            if (sequence != _loadSequence)
            {
                return;
            }

            // 提交本页未加载条目解码：reset 时视口尚未上报，先走首屏批同步等待保撤层时序，
            // 余量交调度器等待视口驱动；翻页时视口就在新页尾部附近，全部交调度器按
            // 「距视口中心」优先级渐进提交，解码量与页大小解耦。
            if (reset)
            {
                await LoadThumbnailsForVisibleItemsAsync(pending, _loadSequence);
            }
            else
            {
                _scheduler.Enqueue(pending);
            }

            if (reset && sequence == _loadSequence)
            {
                // 撤层点移到缩略图整页就绪之后（用户方案）：等待期间覆盖层显示进度与文字，
                // 撤层时内容一次性完整呈现——替代此前「骨架屏逐张渐入」的顿挫观感。
                // 极端挂起由 ThumbnailWaitTimeout（批次收口）与下方 finally 兜底撤层保底。
                await _dispatcherQueue.EnqueueAsync(() =>
                {
                    IsQuerying = false;
                    IsLoadFailed = false;
                });
            }

        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            if (sequence == _loadSequence)
            {
                await _dispatcherQueue.EnqueueAsync(() =>
                {
                    StatusText = "加载失败，请重试";
                    IsLoadFailed = true;
                    IsQuerying = false;
                });
            }
        }
        finally
        {
            // 只有仍是最新的请求才能收尾，避免旧请求提前关闭新请求的加载状态。
            if (sequence == _loadSequence)
            {
                // await EnqueueAsync 的延续会落回线程池线程，IsLoading 触发的 PropertyChanged
                // 必须回到 UI 线程，故这里也要经过调度。
                await _dispatcherQueue.EnqueueAsync(() =>
                {
                    IsLoading = false;
                    LoadMoreCommand.NotifyCanExecuteChanged();

                    // 兜底撤层：覆盖层是不透明全内容区遮罩，任何提前退出路径（作废 return、
                    // 静默异常、取消传播）只要漏掉显式撤层，它就会永久卡在 Visible——
                    // 此后布局照常、日志照常，唯独所有点击被遮罩吞掉，表现为「点了没反应」。
                    // 正常路径已显式撤过，此处幂等；非 reset 加载本就未显示，同样无害。
                    if (IsQuerying)
                    {
                        IsQuerying = false;
                    }
                });
            }
        }
    }

    /// <summary>并发预取条目尺寸，使布局在缩略图解码完成前就按真实宽高比排列。</summary>
    /// <param name="items">待预取的条目。</param>
    /// <param name="cancellationToken">代数级取消令牌；切换视图后旧预取立即停止。</param>
    /// <param name="sequence">发起时的加载代数；写回前复核，过期请求跳过整块写回
    /// （宽高比通知会驱动全量重排，过期写回纯属 UI 线程浪费）。</param>
    private async Task PrefetchDimensionsAsync(
        IReadOnlyList<MediaItemViewModel> items,
        int sequence,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<(MediaItemViewModel Item, int Width, int Height)>();

        // 探测与属性读取都不触碰 DependencyObject，可在线程池并行；只写回 UI 线程。
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DimensionPrefetchConcurrency,
                CancellationToken = cancellationToken
            },
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
        // 写回前复核代数：探测耗时 1~3 秒，期间切走时过期写回只会白白驱动一轮全量重排。
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            if (sequence != _loadSequence)
            {
                return;
            }

            foreach (var (item, width, height) in results)
            {
                item.SetDimensions(width, height);
            }
        });
    }

    /// <summary>为重置路径的首屏分批加载缩略图：首屏批同步等待保撤层时序，余量交调度器。</summary>
    /// <param name="pending">待加载缩略图的条目（本页全部新增）。</param>
    /// <param name="sequence">发起时的加载代数；切换视图后立即中止首屏批。</param>
    /// <remarks>
    /// 位图创建（SetSourceAsync 与视觉状态切换）都在 UI 线程执行，一次性提交整页 200 条
    /// 会让 UI 线程被解码回调与重排钉死数秒——表现为菜单点击排队（卡顿）。
    /// 首屏批（约 60 条）小批推进并等待完成，返回时首屏已就绪，调用方随即撤层；
    /// 首屏外的条目只入调度器待解队列，滚动到视口时才提交（解码量 = O(视口)）。
    /// </remarks>
    private async Task LoadThumbnailsForVisibleItemsAsync(IReadOnlyList<MediaItemViewModel> pending, int sequence)
    {
        if (pending.Count == 0)
        {
            return;
        }

        // 首屏优先：虚拟化下可见约 40 条，先保证首屏出图。
        await SubmitThumbnailBatchesAsync(pending.Take(FirstScreenSubmitCount).ToList(), sequence);

        if (pending.Count <= FirstScreenSubmitCount || sequence != _loadSequence)
        {
            return;
        }

        // 余量入调度器待解队列：等待视口更新驱动，不立即提交。
        _scheduler.Enqueue(pending.Skip(FirstScreenSubmitCount).ToList());
    }

    /// <summary>把一批条目切成小批提交：每批仅 10 条，批间让出 UI 线程，并等待本组全部完成。</summary>
    private async Task SubmitThumbnailBatchesAsync(IReadOnlyList<MediaItemViewModel> items, int sequence)
    {
        var tasks = new List<Task>(items.Count);

        for (var offset = 0; offset < items.Count; offset += ThumbnailBatchSize)
        {
            // 切换视图后立即中止剩余批：旧请求不再占用解码信号量与 UI 线程。
            if (sequence != _loadSequence)
            {
                return;
            }

            var batch = items.Skip(offset).Take(ThumbnailBatchSize).ToList();

            // EnsureThumbnailAsync 内部会创建 BitmapImage（DependencyObject，具线程亲和性），
            // 必须在 UI 线程发起；此处只收集任务，不能在 lambda 内 await，否则会自我死锁。
            await _dispatcherQueue.EnqueueAsync(() =>
            {
                foreach (var item in batch)
                {
                    tasks.Add(item.EnsureThumbnailAsync(_thumbnailSize));
                }
            });

            // 批间让出 UI 线程：间隔内输入事件与渲染可插队，
            // 把「UI 被连续钉死数秒」化为「平滑渐进」。
            await Task.Delay(ThumbnailBatchGap).ConfigureAwait(false);
        }

        // 等待本组全部完成：防上一页解码与下一页请求叠加，队列越滚越长。
        // 超时仅让收口（防单条 IO 挂死拖死 IsLoading/CanLoadMore），
        // 解码任务仍在后台推进，就绪后由属性通知自然上屏。
        try
        {
            await Task.WhenAll(tasks).WaitAsync(ThumbnailWaitTimeout);
        }
        catch (TimeoutException)
        {
            // 超时仅让收口（防单条 IO 挂死拖死 IsLoading/CanLoadMore），
            // 解码任务仍在后台推进，就绪后由属性通知自然上屏。
        }
        catch (Exception)
        {
            // 积压批以弃任务方式运行，此处必须吞掉异常防未观察异常炸进程。
        }
    }

    /// <summary>刷新页头统计：反映当前筛选结果（类型 / 收藏 / 搜索），而非全库。</summary>
    /// <remarks>
    /// 已指定 kind 时另一侧必然为 0，直接短路，省掉一次 COUNT；
    /// 两类计数并行执行（仓储每次调用独立连接），不再逐个串行等待。
    /// loadSequence 用于代数校验：统计在途期间用户切换视图 / 筛选时，过期结果不得回写。
    /// </remarks>
    private async Task RefreshStatisticsAsync(int sequence)
    {
        try
        {
            var photoCountTask = _kindFilter is MediaKind.Video
                ? Task.FromResult(0)
                : _mediaItems.CountByQueryAsync(CurrentQuery with { Kind = MediaKind.Image });

            var videoCountTask = _kindFilter is MediaKind.Image
                ? Task.FromResult(0)
                : _mediaItems.CountByQueryAsync(CurrentQuery with { Kind = MediaKind.Video });

            await Task.WhenAll(photoCountTask, videoCountTask).ConfigureAwait(false);

            if (sequence != _loadSequence)
            {
                return;
            }

            await _dispatcherQueue.EnqueueAsync(() =>
            {
                PhotoTotal = photoCountTask.Result;
                VideoTotal = videoCountTask.Result;
                OnPropertyChanged(nameof(StatisticsText));
            });
        }
        catch (Exception)
        {
            // 统计为后台旁路任务：失败静默保留上次页头数字，但必须吞掉异常，
            // 否则 fire-and-forget 的未观察异常会在终结线程上炸进程。
        }
    }
}
