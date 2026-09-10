/**
 * 图库视图模型删除聚合链路的离线单元测试（审计 7.9 遗留闭环）。
 * 职责：锁定「回收站成功 → 集合移除 → 索引清理 → 统计收缩 → 通知条结果」全链路语义，
 *      以及部分失败、全部失败与选中项清理等分支。
 * 复用约定：回收站经注入的 IRecycleBinService 桩控制成败（绝不触碰真实回收站）；
 *          仓储用内存桩记录 DeleteByPathsAsync 的实际入参；调度队列同步内联执行。
 * 关键约束：断言「界面移除」与「索引清理」两条线必须同时成立——只删界面留幽灵条目，
 *          或只清索引界面残留，都是缺陷。
 */

using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Xunit;

namespace Pixbian.UI.Tests.ViewModels;

/// <summary>GalleryViewModel 删除聚合链路测试。</summary>
public sealed class GalleryViewModelDeleteTests
{
    /// <summary>媒体条目内存仓储：记录索引清理入参（测试数据恒小于页大小，忽略分页）。</summary>
    private sealed class InMemoryMediaItemRepository : IMediaItemRepository
    {
        public List<MediaItem> Items { get; } = [];

        /// <summary>DeleteByPathsAsync 实际收到的路径批次记录。</summary>
        public List<IReadOnlyList<string>> DeletedPathBatches { get; } = [];

        public Task UpsertBatchAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken = default)
        {
            Items.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetPathsUnderDirectoryAsync(
            string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Items.Select(i => i.Path).Where(p => p.StartsWith(directory, StringComparison.Ordinal))]);

        public async IAsyncEnumerable<string> EnumeratePathsUnderDirectoryAsync(
            string directory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            foreach (var path in Items
                         .Select(i => i.Path)
                         .Where(p => p.StartsWith(directory, StringComparison.Ordinal)))
            {
                yield return path;
            }
        }

        public Task DeleteByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            DeletedPathBatches.Add(paths);
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
            var matched = Items
                .Where(i => query.Kind is null || i.Kind == query.Kind)
                .ToList();
            return Task.FromResult<IReadOnlyList<MediaItem>>(matched);
        }

        public Task SetFavoriteAsync(
            IReadOnlyList<long> ids, bool isFavorite, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

    /// <summary>回收站服务桩：按预置队列逐个返回成败，记录发送路径。</summary>
    private sealed class ScriptedRecycleBin : IRecycleBinService
    {
        private readonly Queue<bool> _results;

        public ScriptedRecycleBin(params bool[] results) => _results = new Queue<bool>(results);

        public List<string> SentPaths { get; } = [];

        public bool SendToRecycleBin(string path)
        {
            SentPaths.Add(path);
            return _results.Count > 0 && _results.Dequeue();
        }
    }

    /// <summary>收藏分组仓储桩：删除链路不触达，空实现。</summary>
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

    private static MediaItem CreateMedia(int id) => new()
    {
        Id = id,
        Path = $"D:\\Lib\\{id}.jpg",
        FileName = $"{id}.jpg",
        Kind = MediaKind.Image
    };

    private static GalleryViewModel CreateViewModel(
        InMemoryMediaItemRepository repository,
        ScriptedRecycleBin recycleBin)
    {
        repository.Items.AddRange(Enumerable.Range(1, 5).Select(CreateMedia));
        return new GalleryViewModel(
            repository,
            new StubFavoriteGroupRepository(),
            new StubThumbnailService(),
            recycleBin,
            new InlineDispatcherQueue());
    }

    [Fact]
    public async Task DeleteFilesAsync_全部成功_集合移除且索引清理与统计同步()
    {
        var repository = new InMemoryMediaItemRepository();
        var recycleBin = new ScriptedRecycleBin(true, true);
        var viewModel = CreateViewModel(repository, recycleBin);
        await viewModel.ReloadAsync();

        var targets = viewModel.Items.Take(2).ToList();

        var (deleted, failed) = await viewModel.DeleteFilesAsync(targets);

        Assert.Equal(2, deleted);
        Assert.Equal(0, failed);

        // 界面与索引两条线同时成立：集合收缩且索引按实际路径清理。
        Assert.Equal(3, viewModel.Items.Count);
        Assert.DoesNotContain(viewModel.Items, i => targets.Contains(i));
        Assert.Single(repository.DeletedPathBatches);
        Assert.Equal(
            targets.Select(i => i.Item.Path).OrderBy(p => p),
            repository.DeletedPathBatches[0].OrderBy(p => p));

        // 页头统计同步收缩（不重载列表）。
        Assert.Equal(3, viewModel.PhotoTotal);

        // 进度推进到终值。
        Assert.Equal(2, viewModel.DeleteProgressValue);
    }

    [Fact]
    public async Task DeleteFilesAsync_部分失败_仅成功条目出集合且通知含失败计数()
    {
        var repository = new InMemoryMediaItemRepository();
        var recycleBin = new ScriptedRecycleBin(true, false, false);
        var viewModel = CreateViewModel(repository, recycleBin);
        await viewModel.ReloadAsync();

        var targets = viewModel.Items.Take(3).ToList();

        var (deleted, failed) = await viewModel.DeleteFilesAsync(targets);

        Assert.Equal(1, deleted);
        Assert.Equal(2, failed);

        // 只有成功条目离开集合；失败的条目原地保留。
        Assert.Equal(4, viewModel.Items.Count);
        Assert.Single(repository.DeletedPathBatches);
        Assert.Single(repository.DeletedPathBatches[0]);

        // 通知条展示部分失败结果。
        Assert.True(viewModel.IsDeleteResultVisible);
        Assert.Contains("1 项", viewModel.DeleteResultText);
        Assert.Contains("2 项无法删除", viewModel.DeleteResultText);
    }

    [Fact]
    public async Task DeleteFilesAsync_全部失败_集合与索引保持原状()
    {
        var repository = new InMemoryMediaItemRepository();
        var recycleBin = new ScriptedRecycleBin(false, false);
        var viewModel = CreateViewModel(repository, recycleBin);
        await viewModel.ReloadAsync();

        var targets = viewModel.Items.Take(2).ToList();

        var (deleted, failed) = await viewModel.DeleteFilesAsync(targets);

        Assert.Equal(0, deleted);
        Assert.Equal(2, failed);

        // 无一成功：不触碰索引，集合原状。
        Assert.Empty(repository.DeletedPathBatches);
        Assert.Equal(5, viewModel.Items.Count);

        // 通知条展示失败原因（首个失败路径）。
        Assert.True(viewModel.IsDeleteResultVisible);
        Assert.Contains("删除失败", viewModel.DeleteResultText);
    }

    [Fact]
    public async Task DeleteFilesAsync_选中项被删除_当前选中置空()
    {
        var repository = new InMemoryMediaItemRepository();
        var recycleBin = new ScriptedRecycleBin(true);
        var viewModel = CreateViewModel(repository, recycleBin);
        await viewModel.ReloadAsync();

        viewModel.SelectedItem = viewModel.Items[0];
        await viewModel.DeleteFilesAsync([viewModel.Items[0]]);

        Assert.Null(viewModel.SelectedItem);
    }
}
