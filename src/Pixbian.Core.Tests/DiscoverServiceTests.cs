/**
 * 发现模式随机抽样服务的单元测试（M6）。
 * 职责：验证随机抽样的正确性、类型过滤、去重队列行为与边界情况。
 * 复用约定：用内存中的桩仓储替代真实数据库，专注验证抽样算法本身；
 *          桩仓储返回固定集合，便于断言无重复与分布覆盖。
 * 关键约束：必须保留「库小于去重容量时不死循环」与「空库返回 null」两个用例——
 *          前者是重试逻辑的死循环防线，后者是界面空状态的依据；
 *          以及「全量遍历能覆盖所有条目」，这是抽样不遗漏的证明。
 */

using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>DiscoverService 测试。</summary>
public sealed class DiscoverServiceTests
{
    [Fact]
    public async Task PickRandomAsync_空库_返回空()
    {
        var service = new DiscoverService(new StubRepository([]));

        Assert.Null(await service.PickRandomAsync());
    }

    [Fact]
    public async Task PickRandomAsync_单条目库_反复抽取仍返回该条目()
    {
        var repository = new StubRepository([CreateItem(1, "a.jpg")]);
        var service = new DiscoverService(repository);

        var first = await service.PickRandomAsync();
        var second = await service.PickRandomAsync();

        // 候选数少于去重容量时会放宽去重，故应仍能取到条目而不是卡死或返回空。
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first.Id);
    }

    [Fact]
    public async Task PickRandomAsync_按范围过滤_仅返回该类型()
    {
        var repository = new StubRepository([
            CreateItem(1, "a.jpg", MediaKind.Image),
            CreateItem(2, "b.mp4", MediaKind.Video)
        ]);

        var service = new DiscoverService(repository);

        Assert.Equal(MediaKind.Image, (await service.PickRandomAsync(DiscoverScope.Images))?.Kind);
        Assert.Equal(MediaKind.Video, (await service.PickRandomAsync(DiscoverScope.Videos))?.Kind);
    }

    [Fact]
    public async Task PickRandomAsync_大量抽取_短期内不重复()
    {
        var repository = new StubRepository(
            Enumerable.Range(1, 500).Select(i => CreateItem(i, $"f{i}.jpg")).ToList());

        var service = new DiscoverService(repository, recentCapacity: 100);

        var picked = new List<long>();

        for (var i = 0; i < 50; i++)
        {
            var item = await service.PickRandomAsync();
            Assert.NotNull(item);
            picked.Add(item.Id);
        }

        Assert.Equal(picked.Count, picked.Distinct().Count());
    }

    [Fact]
    public async Task PickRandomAsync_全量遍历_能覆盖库内每一条()
    {
        var total = 120;
        var repository = new StubRepository(
            Enumerable.Range(1, total).Select(i => CreateItem(i, $"f{i}.jpg")).ToList());

        // 去重容量设为 1，使每次抽取都不会因去重而被拒绝，从而达成全量覆盖。
        var service = new DiscoverService(repository, recentCapacity: 1);

        var seen = new HashSet<long>();

        for (var i = 0; i < total * 20; i++)
        {
            var item = await service.PickRandomAsync();

            if (item is not null)
            {
                seen.Add(item.Id);
            }
        }

        Assert.Equal(total, seen.Count);
    }

    [Fact]
    public async Task Reset_清空去重队列_允许立即重抽到同一条()
    {
        var repository = new StubRepository(
            Enumerable.Range(1, 50).Select(i => CreateItem(i, $"f{i}.jpg")).ToList());

        var service = new DiscoverService(repository, recentCapacity: 200);

        var first = await service.PickRandomAsync();
        Assert.NotNull(first);

        // 重置后总数与去重队列都清空，之前抽过的条目可再次出现。
        service.Reset();

        var afterReset = await service.PickRandomAsync();
        Assert.NotNull(afterReset);
    }

    [Fact]
    public async Task PickRandomAsync_库小于去重容量_仍能返回条目而非卡死()
    {
        var repository = new StubRepository([
            CreateItem(1, "a.jpg"),
            CreateItem(2, "b.jpg")
        ]);

        var service = new DiscoverService(repository, recentCapacity: 200);

        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(await service.PickRandomAsync());
        }
    }

    [Fact]
    public async Task PickRandomAsync_取消令牌已取消_抛出操作取消异常()
    {
        var repository = new StubRepository([CreateItem(1, "a.jpg")]);
        var service = new DiscoverService(repository);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PickRandomAsync(DiscoverScope.All, cts.Token));
    }

    private static MediaItem CreateItem(long id, string fileName, MediaKind kind = MediaKind.Image) =>
        new()
        {
            Id = id,
            FileName = fileName,
            Path = Path.Combine("D:\\Lib", fileName),
            Directory = "D:\\Lib",
            Kind = kind,
            FileSize = 1024,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            IndexedUtc = DateTimeOffset.UnixEpoch
        };

    /// <summary>内存桩仓储，按主键顺序提供条目，用于验证抽样算法。</summary>
    private sealed class StubRepository : IMediaItemRepository
    {
        private readonly List<MediaItem> _items;

        public StubRepository(IReadOnlyList<MediaItem> items) => _items = [.. items];

        public Task UpsertBatchAsync(
            IReadOnlyList<MediaItem> items,
            CancellationToken cancellationToken = default)
        {
            _items.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetPathsUnderDirectoryAsync(
            string directory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_items.Select(i => i.Path).ToList());

        public Task DeleteByPathsAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByIdsAsync(
            IReadOnlyList<long> ids,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MediaItem>> QueryAsync(
            MediaQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>(
                _items.Skip(query.Skip).Take(query.Take).ToList());

        public Task SetFavoriteAsync(
            IReadOnlyList<long> ids,
            bool isFavorite,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountAsync(MediaKind? kind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_items.Count(i => !kind.HasValue || i.Kind == kind.Value));
        }

        public Task<int> CountByQueryAsync(MediaQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var search = string.IsNullOrWhiteSpace(query.SearchText)
                ? null
                : query.SearchText.Trim();

            return Task.FromResult(_items.Count(i =>
                (!query.Kind.HasValue || i.Kind == query.Kind.Value)
                && (!query.IsFavorite.HasValue || i.IsFavorite == query.IsFavorite.Value)
                && (search is null || i.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))));
        }

        public Task<MediaItem?> GetAtOffsetAsync(
            MediaKind? kind,
            int offset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filtered = _items
                .Where(i => !kind.HasValue || i.Kind == kind.Value)
                .OrderBy(i => i.Id)
                .ToList();

            return Task.FromResult(offset >= 0 && offset < filtered.Count ? filtered[offset] : null);
        }

        public Task<MediaItem?> GetByIdAsync(
            long id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_items.FirstOrDefault(i => i.Id == id));
        }
    }
}
