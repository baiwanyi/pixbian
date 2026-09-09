/**
 * 图库视图模型（GalleryViewModel）的离线单元测试。
 * 职责：锁定筛选 reload 流、收藏切换的条目与仓储同步等核心集合语义，
 *      为后续巨型文件拆分与数据层虚拟化提供行为基线。
 * 复用约定：经 IDispatcherQueue 假实现同步内联执行投递（无 UI 线程依赖）；
 *          仓储用内存桩（QueryAsync 忽略分页返回全部匹配条目，测试数据量恒小于页大小）。
 * 关键约束：收藏切换断言「仓储调用 + 条目原位更新」两条线都必须成立——
 *          只改界面不落库或只落库不刷界面都是缺陷。
 */

using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Xunit;

namespace Pixbian.UI.Tests.ViewModels;

/// <summary>GalleryViewModel 离线测试。</summary>
public sealed class GalleryViewModelTests
{
    /// <summary>媒体条目内存仓储：QueryAsync 仅按 Kind 过滤（测试数据恒小于页大小，忽略分页）。</summary>
    private sealed class InMemoryMediaItemRepository : IMediaItemRepository
    {
        public List<MediaItem> Items { get; } = [];

        public List<long> FavoriteCalls { get; } = [];

        public MediaQuery? LastQuery { get; private set; }

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
            LastQuery = query;
            var matched = Items
                .Where(i => query.Kind is null || i.Kind == query.Kind)
                .ToList();
            return Task.FromResult<IReadOnlyList<MediaItem>>(matched);
        }

        public Task SetFavoriteAsync(
            IReadOnlyList<long> ids, bool isFavorite, CancellationToken cancellationToken = default)
        {
            FavoriteCalls.AddRange(ids);

            // MediaItem 的属性是 init-only：以 record with 表达式原位替换，保持列表顺序。
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

    /// <summary>收藏分组内存仓储：仅记录清空成员关系的调用。</summary>
    private sealed class InMemoryFavoriteGroupRepository : IFavoriteGroupRepository
    {
        public List<IReadOnlyList<long>> ClearedMemberships { get; } = [];

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

        public Task ClearMembershipAsync(IReadOnlyList<long> mediaIds, CancellationToken cancellationToken = default)
        {
            ClearedMemberships.Add(mediaIds);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetGroupIdsByMediaAsync(
            IReadOnlyList<long> mediaIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<long>>>(new Dictionary<long, IReadOnlyList<long>>());
    }

    /// <summary>缩略图服务桩：无位图产物，仅承载淘汰事件订阅。</summary>
    private sealed class StubThumbnailService : IThumbnailService
    {
        public event Action<string>? ThumbnailEvicted;

        public double RasterizationScale { get; set; } = 1.0;

        public Task<BitmapImage?> GetThumbnailAsync(
            string path, int size, CancellationToken cancellationToken = default) =>
            Task.FromResult<BitmapImage?>(null);

        public void Release(string path) { }

        public void Invalidate(string path) { }

        public Task<(int Width, int Height)?> GetDimensionsAsync(
            string path, CancellationToken cancellationToken = default) => Task.FromResult<(int, int)?>(null);

        public void RaiseEvicted(string path) => ThumbnailEvicted?.Invoke(path);
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

    private static MediaItem CreateMedia(long id, MediaKind kind, bool isFavorite = false) => new()
    {
        Id = id,
        Path = $"D:\\Lib\\{id}.jpg",
        FileName = $"{id}.jpg",
        Kind = kind,
        IsFavorite = isFavorite
    };

    private static GalleryViewModel CreateViewModel(
        InMemoryMediaItemRepository repository,
        InMemoryFavoriteGroupRepository? favoriteGroups = null) => new(
        repository,
        favoriteGroups ?? new InMemoryFavoriteGroupRepository(),
        new StubThumbnailService(),
        new InlineDispatcherQueue());

    [Fact]
    public async Task ReloadAsync_加载首页_集合按仓储内容填充()
    {
        var repository = new InMemoryMediaItemRepository();
        repository.Items.AddRange(
        [
            CreateMedia(1, MediaKind.Image),
            CreateMedia(2, MediaKind.Image),
            CreateMedia(3, MediaKind.Video)
        ]);
        var viewModel = CreateViewModel(repository);

        await viewModel.ReloadAsync();

        Assert.Equal(3, viewModel.Items.Count);
    }

    [Fact]
    public async Task ApplyKindFilterAsync_视频筛选_查询条件携带类型()
    {
        var repository = new InMemoryMediaItemRepository();
        repository.Items.AddRange(
        [
            CreateMedia(1, MediaKind.Image),
            CreateMedia(2, MediaKind.Video)
        ]);
        var viewModel = CreateViewModel(repository);

        await viewModel.ApplyKindFilterAsync(MediaKind.Video);

        Assert.Equal(MediaKind.Video, repository.LastQuery?.Kind);
        Assert.Single(viewModel.Items);
        Assert.Equal(2, viewModel.Items[0].Id);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_收藏切换_条目原位更新且仓储同步()
    {
        var repository = new InMemoryMediaItemRepository();
        repository.Items.Add(CreateMedia(7, MediaKind.Image));
        var favoriteGroups = new InMemoryFavoriteGroupRepository();
        var viewModel = CreateViewModel(repository, favoriteGroups);
        await viewModel.ReloadAsync();
        var item = viewModel.Items[0];

        await viewModel.ToggleFavoriteAsync(item);

        Assert.True(item.Item.IsFavorite);
        Assert.True(item.IsFavorite);
        Assert.Contains(7, repository.FavoriteCalls);

        // 取消收藏须解除全部分组归属（分组是收藏之下的再分类）。
        await viewModel.ToggleFavoriteAsync(item);
        Assert.False(item.Item.IsFavorite);
        Assert.Single(favoriteGroups.ClearedMemberships);
    }
}
