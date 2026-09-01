/**
 * 媒体元数据后台回填的测试（M3）。
 * 职责：验证探测结果能正确写回索引、失败条目会被收敛为「已失败」而非反复重试，
 *      以及写回既不清除既有宽高、也不触碰收藏等用户数据。
 * 复用约定：与 MediaQueryTests 一致，每个用例独立建临时库并在 Dispose 中清理 WAL 附属文件；
 *          探测器用桩实现替换，测试全程不触碰真实文件与任何平台解码 API。
 * 关键约束：失败条目必须离开待处理集合——这是回填能够收敛的前提，必须有专门用例覆盖；
 *          桩实现被并发调用，记录调用的容器必须是线程安全的。
 */

using System.Collections.Concurrent;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

public sealed class MediaMetadataBackfillTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteMediaItemRepository _repository;

    /// <summary>创建临时数据库并完成迁移。</summary>
    public MediaMetadataBackfillTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "Pixbian.Tests",
            $"{Guid.NewGuid():N}.db");

        var initializer = new SqliteDatabaseInitializer(_databasePath);
        initializer.Initialize();
        _repository = new SqliteMediaItemRepository(initializer.ConnectionString);
    }

    /// <summary>清理临时数据库及其 WAL 附属文件。</summary>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _databasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task BackfillBatchAsync_探测成功_写回宽高且条目离开待处理集合()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        var probe = new StubProbe(new Dictionary<string, MediaMetadataProbeResult?>
        {
            ["D:\\Lib\\a.jpg"] = new(1920, 1080, null),
            ["D:\\Lib\\b.jpg"] = new(800, 600, null)
        });

        var processed = await new MediaMetadataBackfillService(_repository, probe).BackfillBatchAsync();

        Assert.Equal(2, processed);

        var items = await _repository.QueryAsync(new MediaQuery());
        var a = items.Single(i => i.FileName == "a.jpg");
        var b = items.Single(i => i.FileName == "b.jpg");

        Assert.Equal(1920, a.Width);
        Assert.Equal(1080, a.Height);
        Assert.Equal(800, b.Width);
        Assert.Equal(600, b.Height);
        Assert.Empty(await _repository.GetMetadataPendingAsync(10));
    }

    [Fact]
    public async Task BackfillBatchAsync_探测失败_置为失败且不再被捞取()
    {
        await _repository.UpsertBatchAsync([CreateItem("D:\\Lib\\broken.jpg")]);

        var service = new MediaMetadataBackfillService(
            _repository,
            new StubProbe(new Dictionary<string, MediaMetadataProbeResult?>()));

        Assert.Equal(1, await service.BackfillBatchAsync());

        // 收敛的关键：失败必须落为「已失败」而非停留在待处理，
        // 否则损坏文件会在每一轮回填里被反复捞取，永远占满批次额度。
        Assert.Empty(await _repository.GetMetadataPendingAsync(10));
    }

    [Fact]
    public async Task BackfillAllAsync_混杂成功与失败_每条只探测一次后收敛()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\ok.jpg"),
            CreateItem("D:\\Lib\\broken.jpg")
        ]);

        var probe = new StubProbe(new Dictionary<string, MediaMetadataProbeResult?>
        {
            ["D:\\Lib\\ok.jpg"] = new(640, 480, null)
        });

        var total = await new MediaMetadataBackfillService(_repository, probe).BackfillAllAsync();

        Assert.Equal(2, total);
        Assert.Equal(2, probe.Calls.Count);
        Assert.Empty(await _repository.GetMetadataPendingAsync(10));
    }

    [Fact]
    public async Task BackfillBatchAsync_视频条目_写回时长与按旋转还原的宽高()
    {
        await _repository.UpsertBatchAsync([CreateItem("D:\\Lib\\clip.mp4", MediaKind.Video)]);

        // 竖拍视频：探测器已按旋转标记交换过宽高，此处校验仓储原样落库而不做二次处理。
        var probe = new StubProbe(new Dictionary<string, MediaMetadataProbeResult?>
        {
            ["D:\\Lib\\clip.mp4"] = new(1080, 1920, 12500)
        });

        await new MediaMetadataBackfillService(_repository, probe).BackfillBatchAsync();

        var clip = (await _repository.QueryAsync(new MediaQuery()))[0];

        Assert.Equal(1080, clip.Width);
        Assert.Equal(1920, clip.Height);
        Assert.Equal(12500, clip.DurationMs);
    }

    [Fact]
    public async Task BackfillBatchAsync_回填后_保留用户收藏状态()
    {
        await _repository.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        var all = await _repository.QueryAsync(new MediaQuery());
        await _repository.SetFavoriteAsync([all[0].Id], true);

        var probe = new StubProbe(new Dictionary<string, MediaMetadataProbeResult?>
        {
            ["D:\\Lib\\a.jpg"] = new(1920, 1080, null)
        });

        await new MediaMetadataBackfillService(_repository, probe).BackfillBatchAsync();

        var updated = (await _repository.QueryAsync(new MediaQuery()))[0];
        Assert.True(updated.IsFavorite);
    }

    [Fact]
    public async Task UpdateMetadataBatchAsync_探测失败_不清除已回填的宽高()
    {
        await _repository.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        var id = (await _repository.QueryAsync(new MediaQuery()))[0].Id;

        await _repository.UpdateMetadataBatchAsync(
            [new MediaMetadataUpdate(id, new MediaMetadataProbeResult(1920, 1080, null), DateTimeOffset.UnixEpoch)]);

        // 后续一次探测失败（结果为 null）不得把先前已探测到的宽高清空。
        await _repository.UpdateMetadataBatchAsync(
            [new MediaMetadataUpdate(id, null, DateTimeOffset.UnixEpoch)]);

        var updated = (await _repository.QueryAsync(new MediaQuery()))[0];

        Assert.Equal(1920, updated.Width);
        Assert.Equal(1080, updated.Height);
    }

    [Fact]
    public async Task BackfillBatchAsync_库为空_返回零且不发起探测()
    {
        var probe = new StubProbe(new Dictionary<string, MediaMetadataProbeResult?>());

        Assert.Equal(0, await new MediaMetadataBackfillService(_repository, probe).BackfillBatchAsync());
        Assert.Empty(probe.Calls);
    }

    private static MediaItem CreateItem(string path, MediaKind kind = MediaKind.Image) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path) ?? string.Empty,
        Kind = kind,
        FileSize = 1024,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        IndexedUtc = DateTimeOffset.UnixEpoch
    };

    /// <summary>按路径返回预设探测结果的桩；未登记的路径一律视为探测失败。</summary>
    private sealed class StubProbe : IMediaMetadataProbe
    {
        private readonly IReadOnlyDictionary<string, MediaMetadataProbeResult?> _results;

        public StubProbe(IReadOnlyDictionary<string, MediaMetadataProbeResult?> results) => _results = results;

        /// <summary>已探测的路径集合；回填是并发的，故必须是线程安全容器。</summary>
        public ConcurrentBag<string> Calls { get; } = [];

        public Task<MediaMetadataProbeResult?> ProbeAsync(
            string path,
            bool isVideo,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(path);
            return Task.FromResult(_results.TryGetValue(path, out var result) ? result : null);
        }
    }
}
