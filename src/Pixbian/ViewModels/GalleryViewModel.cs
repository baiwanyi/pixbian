/**
 * 图库页视图模型（主文件）：筛选、集合与分页加载。
 * 职责：维护排序/筛选/搜索状态并按条件分页加载媒体条目，管理集合替换与页头统计；
 *      删除链路（回收站 + 通知条）见 GalleryViewModel.Delete.cs，
 *      缩略图调度与尺寸预取管线见 GalleryViewModel.Thumbnails.cs。
 * 复用约定：数据访问全部经 IMediaItemRepository，禁止在此编写 SQL 或直接触碰文件系统；
 *          列表查询与页头统计共用 CurrentQuery，保证两处条件同源、数字与内容一致。
 * 关键约束：分页为追加模式，切换筛选或搜索时必须先清空集合并把 Skip 归零，否则会串页；
 *          非随机排序翻页以已加载末条构造键集游标，删除收缩集合后游标天然落在新末条上；
 *          随机排序由固定 random_rank 游标分页，翻页自上一页末条之后推进。
 */

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

    /// <summary>切换视图触发后台强制 GC 的已释放条目数阈值：低于该值交由常规 GC 自然回收，
    /// 避免为几百 KB 的回收付出全堆标记的停顿。</summary>
    private const int AggressiveGcItemThreshold = 300;

    /// <summary>
    /// 切换视图后触发内存压缩的托管堆阈值：低于此值跳过 GC——
    /// blocking 式收集是 STW（冻结含 UI 线程在内的全部托管线程），只有在
    /// 位图大量驻留、不压缩会演变为分配风暴时才值得付出这笔停顿。
    /// </summary>
    private const long MemoryCompactionThresholdBytes = 1024L * 1024 * 1024;

    /// <summary>随机序值的取模上界，须与 Schema v4 触发器/回填的表达式严格一致。</summary>
    private const long RandomRankModulus = 2147483647;

    private readonly IMediaItemRepository _mediaItems;
    private readonly IFavoriteGroupRepository _favoriteGroups;
    private readonly IThumbnailService _thumbnails;
    private readonly IRecycleBinService _recycleBin;
    private readonly IUiDispatcher _dispatcherQueue;

    /// <summary>宽高比批量写回完成（UI 线程触发）：等高虚拟化布局据此重建行几何表。</summary>
    public event EventHandler? AspectRatiosApplied;

    /// <summary>条目集合被整体替换（切目录 / 筛选 / 搜索 / 重载）时触发。
    /// 翻页追加与删除只发 ItemCount 通知，不发本事件——页面据此区分「替换需回顶重建」
    /// 与「追加/收缩须保持滚动位置」两种语义。</summary>
    public event EventHandler? ItemsReplaced;

    /// <summary>等高视图（ItemsRepeater）选择服务：以条目引用维护选中集合并回写 IsSelected。</summary>
    public GallerySelectionService JustifiedSelection { get; }

    private MediaKind? _kindFilter;
    private bool _onlyFavorites;
    private long? _categoryFilter;
    private string? _categoryName;

    /// <summary>收藏分组主键；有值时只显示该分组内的收藏条目。</summary>
    private long? _favoriteGroupId;

    /// <summary>是否只显示「已收藏但未归入任何分组」的条目。</summary>
    private bool _onlyUngrouped;

    private string? _favoriteGroupName;
    private string? _directoryPath;
    private string? _directoryName;
    private MediaSortKey _sortKey = MediaSortKey.ModifiedDate;
    private SortDirection _sortDirection = SortDirection.Descending;
    private int _randomSeed;

    /// <summary>随机浏览的游标（random_rank 起点，含端点）；视图重置（洗牌）时生成，翻页时推进。
    /// 取值域与 Schema v4 的 rank 一致：[0, RandomRankModulus - 1]。</summary>
    private long? _randomCursor;
    private string _searchText = string.Empty;

    private ObservableCollection<MediaItemViewModel> _items = [];

    /// <summary>条目集合，供界面做增量虚拟化展示。</summary>
    /// <remarks>
    /// 重置（切换文件夹/导航）时整体替换实例而非原地清空再逐条添加：新集合尚无绑定订阅者，
    /// 填充不触发任何集合通知，随后一次属性通知完成 ItemsSource 整体替换，界面只重建一轮。
    /// 原地逐条添加会为每条付一次双视图集合通知，切换文件夹时的 UI 卡顿主要来自这里。
    /// </remarks>
    public ObservableCollection<MediaItemViewModel> Items => _items;

    /// <summary>原地 Clear + Add 替换条目内容（保持集合实例）。
    /// 让 ItemsRepeater 走 CollectionChanged 路径（Reset + Add）：由框架正确处理清空旧元素与按新集合
    /// realize，与方形 GridView 的 ContainerContentChanging 兜底路径一致；无需页面层手动
    /// ItemsSource=null/new 重建，避免旧元素挂着上一列表缩略图、ForceCreate 后旧元素不释放等
    /// ItemsRepeater 复用残留类问题。
    /// 仅补发 ItemCount 通知：Items 实例未变，x:Bind 无需重新赋值。</summary>
    private void ReplaceItemsCore(IReadOnlyList<MediaItemViewModel> fresh)
    {
        _items.Clear();
        foreach (var item in fresh)
        {
            _items.Add(item);
        }

        _deferredEvictions.Clear();
        OnPropertyChanged(nameof(ItemCount));
        ItemsReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>数据库侧累计读取的条目数（分页游标）。删除只收缩界面集合、不回退该游标，
    /// 否则增量分页的 Skip 与数据库偏移错位，已展示的条目会被重复拉取。</summary>
    private int _loadedCount;

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

    public GalleryViewModel(
        IMediaItemRepository mediaItems,
        IFavoriteGroupRepository favoriteGroups,
        IThumbnailService thumbnails,
        IRecycleBinService recycleBin,
        IUiDispatcher? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(favoriteGroups);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(recycleBin);

        _mediaItems = mediaItems;
        _favoriteGroups = favoriteGroups;
        _thumbnails = thumbnails;
        _recycleBin = recycleBin;
        _dispatcherQueue = dispatcherQueue ?? new UiDispatcherAdapter(DispatcherQueue.GetForCurrentThread());
        _scheduler = new ThumbnailLoadScheduler(() => Items, () => _thumbnailSize, _dispatcherQueue.CreateTimer());
        JustifiedSelection = new GallerySelectionService(() => Items);
        _thumbnails.ThumbnailEvicted += OnThumbnailEvicted;
    }

    /// <summary>当前条目总数。</summary>
    public int ItemCount => Items.Count;

    /// <summary>页头标题：随导航目标（图库 / 视频 / 收藏夹）变化；按文件夹或分类过滤时显示其名称。</summary>
    public string PageTitle =>
        _directoryName
        ?? _categoryName
        ?? _favoriteGroupName
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
        FavoriteGroupId = _favoriteGroupId,
        OnlyUngrouped = _onlyUngrouped,
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
        ResetFavoriteGroupFilter();
        OnPropertyChanged(nameof(PageTitle));
        return ReloadAsync();
    }

    /// <summary>应用「仅收藏」筛选并重新加载。</summary>
    /// <param name="onlyFavorites">是否仅显示收藏条目。</param>
    public Task ApplyFavoritesOnlyAsync(bool onlyFavorites)
    {
        _onlyFavorites = onlyFavorites;
        ResetFavoriteGroupFilter();
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
        ResetFavoriteGroupFilter();
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
        ResetFavoriteGroupFilter();
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
        ResetFavoriteGroupFilter();
        NotifyFilterChanged();
        return ReloadAsync();
    }

    /// <summary>应用收藏分组过滤并重新加载：显示已归入该分组的收藏条目。</summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="groupName">分组名称，用于页头标题。</param>
    public Task ApplyFavoriteGroupFilterAsync(long groupId, string groupName) =>
        ApplyFavoriteGroupCoreAsync(groupId, ungrouped: false, groupName);

    /// <summary>应用「未分组」过滤并重新加载：显示已收藏但未归入任何分组的条目。</summary>
    /// <param name="groupName">展示名称，用于页头标题。</param>
    public Task ApplyUngroupedFavoritesFilterAsync(string groupName) =>
        ApplyFavoriteGroupCoreAsync(groupId: null, ungrouped: true, groupName);

    /// <summary>分组维度的统一入口：分组是「收藏之下的再分类」，故一律置上「仅收藏」。</summary>
    /// <param name="groupId">分组主键；为 null 时须配合 ungrouped 使用。</param>
    /// <param name="ungrouped">是否只取未分组的收藏条目。</param>
    /// <param name="groupName">页头标题。</param>
    private Task ApplyFavoriteGroupCoreAsync(long? groupId, bool ungrouped, string groupName)
    {
        _kindFilter = null;
        _onlyFavorites = true;
        _categoryFilter = null;
        _categoryName = null;
        _directoryPath = null;
        _directoryName = null;
        _favoriteGroupId = groupId;
        _onlyUngrouped = ungrouped;
        _favoriteGroupName = groupName;
        NotifyFilterChanged();
        return ReloadAsync();
    }

    /// <summary>清空收藏分组维度；除分组入口外的全部筛选切换都必须调用，
    /// 否则分组条件会与分类 / 文件夹等维度叠加，表现为「在某个分类里只看到某个分组的条目」。</summary>
    private void ResetFavoriteGroupFilter()
    {
        _favoriteGroupId = null;
        _onlyUngrouped = false;
        _favoriteGroupName = null;
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

        // 取消收藏即解除全部分组归属：分组是「收藏之下的再分类」，
        // 收藏已取消却仍留在分组里，会让分组视图出现查不到的幽灵成员。
        if (!target)
        {
            await _favoriteGroups.ClearMembershipAsync([id]);
        }

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

    /// <summary>把选中条目加入或移出指定收藏分组；加入隐含置收藏。
    /// 不整页重载：选择模式下用户会连续勾多个分组，重载会清空选择并打断操作。</summary>
    /// <param name="items">选中条目。</param>
    /// <param name="groupId">分组主键。</param>
    /// <param name="isMember">true 为加入，false 为移出。</param>
    public async Task ApplySelectionGroupAsync(
        IReadOnlyList<MediaItemViewModel> items,
        long groupId,
        bool isMember)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(i => i.Id).ToList();
        await _favoriteGroups.SetMembershipAsync(groupId, ids, isMember);

        // 移出分组不动收藏状态，条目显示无需回写。
        if (isMember)
        {
            await MarkSelectionFavoritedAsync(items);
        }
    }

    /// <summary>把选中条目标记为「仅收藏、不分组」：清除全部分组归属并置收藏。</summary>
    /// <param name="items">选中条目。</param>
    public async Task ApplySelectionUngroupedAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(i => i.Id).ToList();
        await _favoriteGroups.ClearMembershipAsync(ids);
        await _mediaItems.SetFavoriteAsync(ids, true);
        await MarkSelectionFavoritedAsync(items);
    }

    /// <summary>把选中条目的收藏态回写到界面；仓储内部 ConfigureAwait(false)，写 UI 须切回 UI 线程。</summary>
    /// <param name="items">选中条目。</param>
    private async Task MarkSelectionFavoritedAsync(IReadOnlyList<MediaItemViewModel> items) =>
        await _dispatcherQueue.EnqueueAsync(() =>
        {
            foreach (var item in items)
            {
                item.SetFavorite(true);
            }
        });

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

    /// <summary>加载请求代数：新请求立即使旧请求过期，旧任务不得再写 UI 或收尾加载状态。</summary>
    private int _loadSequence;

    /// <summary>与代数配套的取消源：切走后立即终止旧请求的尺寸预取，停止继续灌文件 IO。</summary>
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
                ReplaceItemsCore(Array.Empty<MediaItemViewModel>());
            }

            if (staleCount > AggressiveGcItemThreshold)
            {
                // 后台线程收集：blocking+compacting 在 GB 级堆上会令 UI 完全暂停数秒
                // （实测即未响应），移到后台后 UI 仅在标记阶段短暂参与。
                // 延迟错峰：切换后的前两秒是首屏容器生成与解码高峰，GC 标记与之
                // 竞争会拖慢切换感知，先让首屏渲染完成再压缩。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));

                    // 仅在内存压力真实存在时才压缩：blocking:true 的 gen2 收集是 STW，
                    // 会冻结全部托管线程（含 UI 线程），「移到后台线程」并不规避停顿；
                    // 常规切换经上方的逐条 Release 已回收位图大头，多数情况无须付这笔停顿。
                    if (GC.GetTotalMemory(forceFullCollection: false) < MemoryCompactionThresholdBytes)
                    {
                        return;
                    }

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
            // 替换块会先于首帧渲染入队执行。
            // 覆盖层通知在点击处理器同步段发出，本身早于任何重活到达界面，无需额外等待。
        }

        try
        {
            // 非随机排序走键集分页：以已加载末条为界继续取，替代 OFFSET 深翻——
            // 后者在数十万条时每页都要全表 CASE 排序并丢弃前 N 行，翻得越深越慢；
            // 随机排序已由 random_rank 索引游标承担同样的职责。
            // 删除收缩集合后游标自动落在当前末条上，天然避免 OFFSET 的错位重复。
            var keyset = _sortKey != MediaSortKey.Random && !reset && _items.Count > 0
                ? BuildKeysetCursor(_items[^1].Item)
                : null;

            // Skip 用与集合数量解耦的游标：删除操作会原地收缩 Items，若以 Items.Count 为偏移，
            // 下一页会与数据库错位、把已展示的条目重复拉取一遍。
            var query = CurrentQuery with
            {
                Skip = reset ? 0 : _loadedCount,
                Take = PageSize,
                RandomCursor = _sortKey == MediaSortKey.Random ? _randomCursor : null,
                Keyset = keyset
            };

            // QueryAsync 内部使用 ConfigureAwait(false)，await 之后当前线程已是线程池线程。
            // ObservableCollection 与 BitmapImage 只能在 UI 线程操作，故必须切回 UI 线程。
            var page = await _mediaItems.QueryAsync(query);

            List<MediaItemViewModel> pending = [];

            // 本页新建的条目：作为缩略图解码的提交来源。
            var added = new List<MediaItemViewModel>(page.Count);

            // 期间又来了新请求：本次结果作废，不触碰集合与加载状态。
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

                    // 集合原地 Clear + Add：让 ItemsRepeater 走 CollectionChanged 路径，由框架
                    // 正确处理「先 Reset 清空旧元素、再 Add realize 新元素」，避免 ItemsSource
                    // 替换引发的 ItemsRepeater 复用残留（首格串内容 / 缩略图复用）。
                    foreach (var item in page)
                    {
                        var vm = new MediaItemViewModel(item, _thumbnails.LoadThumbnailAsyncCore);
                        added.Add(vm);
                    }

                    ReplaceItemsCore(added);
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
                    // 不按索引做头部瘦身——解码量已由调度器收敛到视口，无「释放→重解」自激。
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
                // 远比「数字晚几百毫秒」重要。
                _ = RefreshStatisticsAsync(sequence);
            }

            // 先定宽高比再加载缩略图：位图到位时宽高比若已与预取值一致就不会重排，
            // 否则每个条目都要先从方图跳到真实比例，整行跟着抖。
            // 只对索引里没有宽高的条目探测文件头：后台元数据回填完成之后这里为空集合，
            // 打开文件夹不产生任何文件 IO，转圈时长只剩一次 SQL 查询与缩略图解码。
            var dimensionPending = pending.Where(i => i.NeedsDimensionProbe).ToList();

            await PrefetchDimensionsAsync(dimensionPending, sequence, prefetchToken);

            if (sequence != _loadSequence)
            {
                return;
            }

            // 提交本页未加载条目解码。reset 时视口尚未上报：首屏批同步等待保撤层时序，余量交调度器。
            // 翻页发生在距底两屏内，新页头部是用户即将进入的区域——同样以批节奏立即提交
            // （fire-and-forget，翻页无撤层无需等待），其余条目交调度器按视口优先级渐进。
            if (reset)
            {
                await LoadThumbnailsForVisibleItemsAsync(pending, _loadSequence);
            }
            else
            {
                _ = LoadThumbnailsForVisibleItemsAsync(pending, _loadSequence);
            }

            if (reset && sequence == _loadSequence)
            {
                // 撤层点移到缩略图整页就绪之后（用户方案）：等待期间覆盖层显示进度与文字，
                // 撤层时内容一次性完整呈现，避免逐张渐入的顿挫观感。
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

    /// <summary>由上一页末条目构造键集分页游标；按当前排序键只填对应字段。</summary>
    /// <param name="item">上一页末条目。</param>
    /// <remarks>
    /// 每种排序键只填游标的对应字段：字段与数据库列的类型一一对应，
    /// 仓储层据此绑定参数，避免「一个 object 装多型值」的装箱与隐式转换。
    /// </remarks>
    private KeysetCursor BuildKeysetCursor(MediaItem item) => _sortKey switch
    {
        MediaSortKey.FileSize => new KeysetCursor(item.Id, LastNumber: item.FileSize),
        MediaSortKey.FileName => new KeysetCursor(item.Id, LastText: item.FileName),
        _ => new KeysetCursor(item.Id, LastUtc: item.ModifiedUtc),
    };

    /// <summary>刷新页头统计：反映当前筛选结果（类型 / 收藏 / 搜索），而非全库。</summary>
    /// <remarks>
    /// 已指定 kind 时另一侧必然为 0，直接短路，省掉一次 COUNT；
    /// 两类计数并行执行（仓储每次调用独立连接），无需逐个串行等待。
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

    /// <inheritdoc />
    /// <remarks>仅释放删除用取消令牌与调度器；删除正常结束时已就地释放并置空，此处兜底应用退出场景。</remarks>
    public void Dispose()
    {
        _thumbnails.ThumbnailEvicted -= OnThumbnailEvicted;
        _scheduler.Dispose();
        _deleteCts?.Dispose();
        _deleteCts = null;
    }
}
