/**
 * 图库视图模型分页与删除守卫的深度单元测试（审计报告 7.8 遗留补充）。
 * 职责：锁定键集分页游标构造、随机游标推进、集合收缩后游标落点与删除守卫等核心分页语义，
 *      为 GalleryViewModel 拆分与数据层虚拟化提供行为基线。
 * 复用约定：经 IDispatcherQueue 假实现同步内联执行投递；仓储用支持 Skip/Take/Keyset/RandomCursor
 *          的分页内存桩，真实模拟「页与页拼接不重不漏」的数据库语义。
 * 关键约束：删除聚合完整链路（回收站 → 集合移除 → 索引清理）依赖 RecycleBinHelper 可注入，
 *          留待拆分时补齐；本文件仅锁定不触碰磁盘的守卫与游标语义。
 */

using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Xunit;

namespace Pixbian.UI.Tests.ViewModels;

/// <summary>GalleryViewModel 分页与删除守卫深度测试。</summary>
public sealed class GalleryViewModelPaginationTests
{
    /// <summary>页大小：与 GalleryViewModel 的私有常量保持一致（半页留余量便于翻页断言）。</summary>
    private const int PageSize = 200;

    /// <summary>
    /// 支持真实分页语义的内存仓储：按 ModifiedUtc 降序排列，键集游标从 LastId 之后续页
    /// （忽略 Skip），随机排序按 RandomRank 升序过滤游标；测试数据规模以「页与页拼接 = 全量
    /// 不重不漏」为断言目标。
    /// </summary>
    private sealed class PagedMediaItemRepository : IMediaItemRepository
    {
        public List<MediaItem> Items { get; } = [];

        /// <summary>历次查询条件记录：游标推进断言的数据源。</summary>
        public List<MediaQuery> Queries { get; } = [];

        public List<long> FavoriteCalls { get; } = [];

        public Task UpsertBatchAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken = default)
        {
            Items.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetPathsUnderDirectoryAsync(
            string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Items.Select(i => i.Path).Where(p => p.StartsWith(directory, StringComparison.Ordinal))]);

        public Task DeleteByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(i => paths.Contains(i.Path));
            return Task.CompletedTask;
        }

        public Task DeleteByIdsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(i => ids.Contains(i.Id));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MediaItem>> QueryAsync(
            MediaQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);

            var matched = Items
                .Where(i => query.Kind is null || i.Kind == query.Kind)
                .Where(i => query.IsFavorite is null || i.IsFavorite == query.IsFavorite);

            if (query.SortKey == MediaSortKey.Random)
            {
                var page = matched
                    .Where(i => i.RandomRank is { } rank
                        && (query.RandomCursor is null || rank >= query.RandomCursor))
                    .OrderBy(i => i.RandomRank)
                    .Take(query.Take)
                    .ToList();
                return Task.FromResult<IReadOnlyList<MediaItem>>(page as IReadOnlyList<MediaItem> ?? page);
            }

            // 键集游标生效时忽略 Skip：从游标条目之后继续取（真实仓储的双键比较语义在此
            // 简化为「按 id 定位续页」，页与页拼接不重不漏的契约不变）。
            var ordered = matched
                .OrderByDescending(i => i.ModifiedUtc)
                .ThenByDescending(i => i.Id)
                .ToList();
            var start = query.Keyset is { } keyset
                ? ordered.FindIndex(i => i.Id == keyset.LastId) + 1
                : query.Skip;
            var window = ordered.Skip(start).Take(query.Take).ToList();
            return Task.FromResult<IReadOnlyList<MediaItem>>(window as IReadOnlyList<MediaItem> ?? window);
        }

        public Task SetFavoriteAsync(
            IReadOnlyList<long> ids, bool isFavorite, CancellationToken cancellationToken = default)
        {
            FavoriteCalls.AddRange(ids);

            for (var i = 0; i < Items.Count; i++)
            {
                if (ids.Contains(Items[i].Id))
                {
                    Items[i] = Items[i] with { IsFavorite = isFavorite };
                }
            }

            return Task.CompletedTask;
        }

        public Task<MediaItem?> GetAtOffsetAsync(
            MediaKind? kind, int offset, CancellationToken cancellationToken = default) =>
            Task.FromResult<MediaItem?>(null);

        public Task<MediaItem?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task<int> CountAsync(MediaKind? kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.Count(i => kind is null || i.Kind == kind));

        public Task<int> CountByQueryAsync(MediaQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.Count);

        public Task<IReadOnlyList<MediaItem>> GetMetadataPendingAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task UpdateMetadataBatchAsync(
            IReadOnlyList<MediaMetadataUpdate> updates,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>收藏分组仓储桩：仅承载清空成员关系调用记录。</summary>
    private sealed class StubFavoriteGroupRepository : IFavoriteGroupRepository
    {
        public Task<IReadOnlyList<FavoriteGroup>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FavoriteGroup>>([]);

        public Task<FavoriteGroup> AddAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FavoriteGroup { Id = 1, Name = name });

        public Task RenameAsync(long id, string name, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetMembershipAsync(
            long groupId, IReadOnlyList<long> mediaIds, bool isMember,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearMembershipAsync(IReadOnlyList<long> mediaIds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetGroupIdsByMediaAsync(
            IReadOnlyList<long> mediaIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<long>>>(
                new Dictionary<long, IReadOnlyList<long>>());
    }

    /// <summary>回收站服务桩：默认全部成功，记录发送路径。</summary>
    private sealed class StubRecycleBin : IRecycleBinService
    {
        public List<string> SentPaths { get; } = [];

        public bool SendToRecycleBin(string path)
        {
            SentPaths.Add(path);
            return true;
        }
    }

    /// <summary>缩略图服务桩：无位图产物，仅承载接口依赖。</summary>
    private sealed class StubThumbnailService : IThumbnailService
    {
        // 本文件不含淘汰链路用例：显式空访问器避免 CS0067 未使用事件告警。
        public event Action<string>? ThumbnailEvicted { add { } remove { } }

        public double RasterizationScale { get; set; } = 1.0;

        public Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> GetThumbnailAsync(
            string path, int size, CancellationToken cancellationToken = default) =>
            Task.FromResult<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?>(null);

        public void Release(string path) { }

        public void Invalidate(string path) { }

        public Task<(int Width, int Height)?> GetDimensionsAsync(
            string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<(int, int)?>(null);
    }

    /// <summary>同步内联调度队列：投递立即执行（测试无 UI 线程约束）。</summary>
    private sealed class InlineDispatcherQueue : IUiDispatcher
    {
        public bool TryEnqueue(Action action)
        {
            action();
            return true;
        }

        public Task EnqueueAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public IUiDispatcherTimer CreateTimer() => new InlineTimer();

        public void Dispose() { }
    }

    private sealed class InlineTimer : IUiDispatcherTimer
    {
        public TimeSpan Interval { get; set; }

        public bool IsRunning { get; private set; }

        // 假计时器不驱动真实节拍：显式空访问器避免 CS0067 未使用事件告警。
        public event EventHandler? Tick { add { } remove { } }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Dispose() { }
    }

    /// <summary>构造测试条目：ModifiedUtc 随 id 递增保证确定排序；RandomRank 取值域顶部，
    /// 保证随机游标（[0, 2^31-1] 随机洗牌）几乎必然落在数据区间之下、第一页非空。</summary>
    private static MediaItem CreatePagedMedia(long id, bool isFavorite = false) => new()
    {
        Id = id,
        Path = $"D:\\Lib\\{id}.jpg",
        FileName = $"{id}.jpg",
        Kind = MediaKind.Image,
        ModifiedUtc = DateTimeOffset.UnixEpoch.AddMinutes(id),
        IsFavorite = isFavorite,
        RandomRank = 2147483646 - 449 + id
    };

    private static GalleryViewModel CreateViewModel(PagedMediaItemRepository repository) => new(
        repository,
        new StubFavoriteGroupRepository(),
        new StubThumbnailService(),
        new StubRecycleBin(),
        new InlineDispatcherQueue());

    [Fact]
    public async Task LoadMoreAsync_多页数据_翻页不重不漏且页满收口()
    {
        var repository = new PagedMediaItemRepository();
        repository.Items.AddRange(Enumerable.Range(1, 450).Select(id => CreatePagedMedia(id)));
        var viewModel = CreateViewModel(repository);

        await viewModel.ReloadAsync();
        Assert.Equal(PageSize, viewModel.Items.Count);
        Assert.True(viewModel.HasMore);

        await viewModel.LoadMoreAsync();
        Assert.Equal(2 * PageSize, viewModel.Items.Count);
        Assert.True(viewModel.HasMore);

        await viewModel.LoadMoreAsync();

        // 450 条全部加载完毕：页未满（50 < 200）即收口，翻页命令不可再执行。
        Assert.Equal(450, viewModel.Items.Count);
        Assert.False(viewModel.HasMore);
        Assert.False(viewModel.LoadMoreCommand.CanExecute(null));

        var ids = viewModel.Items.Select(i => i.Id).ToHashSet();
        Assert.Equal(450, ids.Count);
        Assert.All(viewModel.Items, i => Assert.Contains(i.Id, repository.Items.Select(r => r.Id)));
    }

    [Fact]
    public async Task LoadMoreAsync_默认排序_翻页携带已加载末条的键集游标()
    {
        var repository = new PagedMediaItemRepository();
        repository.Items.AddRange(Enumerable.Range(1, 450).Select(id => CreatePagedMedia(id)));
        var viewModel = CreateViewModel(repository);

        await viewModel.ReloadAsync();

        // 首页查询不携带键集游标（走 OFFSET/全量起点）。
        Assert.Null(repository.Queries[0].Keyset);

        var lastOfFirstPage = viewModel.Items[^1];
        await viewModel.LoadMoreAsync();

        // 翻页游标必须以已加载末条为界：LastId 与排序键字段（ModifiedUtc）同时携带。
        var keyset = repository.Queries[1].Keyset;
        Assert.NotNull(keyset);
        Assert.Equal(lastOfFirstPage.Id, keyset.LastId);
        Assert.Equal(lastOfFirstPage.Item.ModifiedUtc, keyset.LastUtc);

        // 页与页拼接不重复。
        Assert.Equal(400, viewModel.Items.Count);
        Assert.Equal(400, viewModel.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task LoadMoreAsync_随机排序_游标自上一页末条之后推进()
    {
        var repository = new PagedMediaItemRepository();
        repository.Items.AddRange(Enumerable.Range(1, 450).Select(id => CreatePagedMedia(id)));
        var viewModel = CreateViewModel(repository);

        await viewModel.ApplySortOrderAsync(MediaSortKey.Random, SortDirection.Ascending);

        // 随机洗牌由 reset 路径生成游标：首页查询必携带。
        Assert.NotNull(repository.Queries[0].RandomCursor);
        Assert.Equal(PageSize, viewModel.Items.Count);

        var lastRankOfFirstPage = viewModel.Items[^1].Item.RandomRank!.Value;

        await viewModel.LoadMoreAsync();

        // 游标推进到本页末条之后：翻页自下一条继续，不重复不遗漏。
        Assert.Equal(lastRankOfFirstPage + 1, repository.Queries[1].RandomCursor);

        var firstPageIds = viewModel.Items.Take(PageSize).Select(i => i.Id).ToHashSet();
        var secondPageIds = viewModel.Items.Skip(PageSize).Select(i => i.Id).ToHashSet();
        Assert.Equal(2 * PageSize, viewModel.Items.Count);
        Assert.Empty(firstPageIds.Intersect(secondPageIds));
    }

    [Fact]
    public async Task 收藏夹视图取消收藏_集合收缩后翻页游标落在新末条()
    {
        var repository = new PagedMediaItemRepository();
        repository.Items.AddRange(Enumerable.Range(1, 250).Select(id => CreatePagedMedia(id, isFavorite: true)));
        var viewModel = CreateViewModel(repository);

        await viewModel.ApplyFavoritesOnlyAsync(true);
        Assert.Equal(PageSize, viewModel.Items.Count);
        Assert.True(viewModel.HasMore);

        // 取消收藏当前末条：集合原地收缩（200 → 199），分页游标不得回退到集合长度。
        var shrinking = viewModel.Items[^1];
        await viewModel.ToggleFavoriteAsync(shrinking);
        Assert.Equal(PageSize - 1, viewModel.Items.Count);

        // 翻页游标必须落在收缩后的新末条上：若实现错用收缩后的集合长度作 OFFSET，
        // 会与数据库错位、把已展示条目重复拉取一遍。
        var cursorAnchorId = viewModel.Items[^1].Id;

        await viewModel.LoadMoreAsync();

        var keyset = repository.Queries[1].Keyset;
        Assert.NotNull(keyset);
        Assert.Equal(cursorAnchorId, keyset.LastId);

        // 页与页拼接不重复：取消收藏的条目既不在集合中也不再被拉取。
        var ids = viewModel.Items.Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(shrinking.Id, ids);
        Assert.Equal(249, ids.Count);
        Assert.False(viewModel.HasMore);
    }

    [Fact]
    public async Task DeleteFilesAsync_空集合或删除进行中_守卫直接返回零()
    {
        var repository = new PagedMediaItemRepository();
        repository.Items.AddRange(Enumerable.Range(1, 5).Select(id => CreatePagedMedia(id)));
        var viewModel = CreateViewModel(repository);
        await viewModel.ReloadAsync();

        var (emptyDeleted, emptyFailed) = await viewModel.DeleteFilesAsync([]);
        Assert.Equal(0, emptyDeleted);
        Assert.Equal(0, emptyFailed);

        // 并发守卫：删除进行中时新的删除请求静默失效（实测过的「删除永久失效」缺陷防线）。
        viewModel.IsDeleteInProgress = true;
        var (guardDeleted, guardFailed) = await viewModel.DeleteFilesAsync([viewModel.Items[0]]);
        Assert.Equal(0, guardDeleted);
        Assert.Equal(0, guardFailed);

        // 守卫路径不启动删除状态机：无结果通知、进度未推进（IsDeleteInProgress 由守卫方保持为真，
        // 通知条随之可见属正常联动）。
        Assert.False(viewModel.IsDeleteResultVisible);
        Assert.Equal(string.Empty, viewModel.DeleteProgressText);
        Assert.Empty(repository.FavoriteCalls);
    }
}
