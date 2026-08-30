/**
 * M2 新增仓储能力的集成测试。
 * 职责：验证分页查询的类型筛选、关键词搜索、排序、收藏与按主键删除。
 * 复用约定：与 M1 的仓储测试共用同一套约定——真实 SQLite 文件、独立临时目录、固定时间戳。
 * 关键约束：必须保留「搜索关键词含 LIKE 通配符」用例，
 *          若搜索值未转义，用户输入 % 或 _ 会被当作模式字符，导致筛选项数与预期不符。
 */

using Pixbian.Core.Models;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>M2 新增仓储方法的集成测试。</summary>
public sealed class MediaQueryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteMediaItemRepository _repository;
    private readonly DateTimeOffset _fixedTime = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>创建临时数据库并完成迁移。</summary>
    public MediaQueryTests()
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
    public async Task QueryAsync_按类型筛选_仅返回该类型()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg", MediaKind.Image),
            CreateItem("D:\\Lib\\b.png", MediaKind.Image),
            CreateItem("D:\\Lib\\c.mp4", MediaKind.Video)
        ]);

        var images = await _repository.QueryAsync(new MediaQuery { Kind = MediaKind.Image });
        var videos = await _repository.QueryAsync(new MediaQuery { Kind = MediaKind.Video });

        Assert.Equal(2, images.Count);
        Assert.Single(videos);
        Assert.All(images, i => Assert.Equal(MediaKind.Image, i.Kind));
    }

    [Fact]
    public async Task QueryAsync_关键词搜索_按文件名匹配()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\IMG_0001.jpg"),
            CreateItem("D:\\Lib\\IMG_0002.jpg"),
            CreateItem("D:\\Lib\\screenshot.png")
        ]);

        var result = await _repository.QueryAsync(new MediaQuery { SearchText = "IMG_" });

        Assert.Equal(2, result.Count);
        Assert.All(result, i => Assert.Contains("IMG_", i.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryAsync_关键词含通配符_按字面量匹配而非模式匹配()
    {
        // 搜索值含 % 与 _，若未转义会被当作通配符，导致百分号匹配任意字符。
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\100%.jpg"),
            CreateItem("D:\\Lib\\100X.jpg")
        ]);

        var result = await _repository.QueryAsync(new MediaQuery { SearchText = "100%" });

        Assert.Single(result);
        Assert.Equal("100%.jpg", result[0].FileName);
    }

    [Fact]
    public async Task QueryAsync_分页_按顺序返回对应区段()
    {
        await _repository.UpsertBatchAsync(Enumerable.Range(0, 10)
            .Select(i => CreateItem($"D:\\Lib\\file{i:D2}.jpg", takenAt: _fixedTime.AddDays(i)))
            .ToList());

        var firstPage = await _repository.QueryAsync(new MediaQuery { Skip = 0, Take = 4 });
        var secondPage = await _repository.QueryAsync(new MediaQuery { Skip = 4, Take = 4 });

        Assert.Equal(4, firstPage.Count);
        Assert.Equal(4, secondPage.Count);
        Assert.Empty(firstPage.Select(f => f.Path).Intersect(secondPage.Select(f => f.Path)));
    }

    [Fact]
    public async Task QueryAsync_按文件名排序_返回升序结果()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\c.jpg"),
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        var result = await _repository.QueryAsync(
            new MediaQuery { SortOrder = MediaSortOrder.FileNameAscending });

        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], result.Select(i => i.FileName));
    }

    [Fact]
    public async Task QueryAsync_按文件大小排序_返回倒序结果()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\small.jpg", fileSize: 100),
            CreateItem("D:\\Lib\\large.jpg", fileSize: 9000),
            CreateItem("D:\\Lib\\medium.jpg", fileSize: 1000)
        ]);

        var result = await _repository.QueryAsync(
            new MediaQuery { SortOrder = MediaSortOrder.FileSizeDescending });

        Assert.Equal(["large.jpg", "medium.jpg", "small.jpg"], result.Select(i => i.FileName));
    }

    [Fact]
    public async Task SetFavoriteAsync_批量更新收藏状态()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        var ids = (await _repository.QueryAsync(new MediaQuery())).Select(i => i.Id).ToList();

        await _repository.SetFavoriteAsync(ids, true);
        var favorited = await _repository.QueryAsync(new MediaQuery());
        Assert.All(favorited, i => Assert.True(i.IsFavorite));

        await _repository.SetFavoriteAsync(ids, false);
        var unfavorited = await _repository.QueryAsync(new MediaQuery());
        Assert.All(unfavorited, i => Assert.False(i.IsFavorite));
    }

    [Fact]
    public async Task DeleteByIdsAsync_按主键删除()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        var all = await _repository.QueryAsync(new MediaQuery());
        await _repository.DeleteByIdsAsync([all[0].Id]);

        var remaining = await _repository.QueryAsync(new MediaQuery());
        Assert.Single(remaining);
        Assert.Equal("b.jpg", remaining[0].FileName);
    }

    [Fact]
    public async Task CountAsync_排除软删除条目()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        Assert.Equal(2, await _repository.CountAsync(null));

        var all = await _repository.QueryAsync(new MediaQuery());
        await _repository.DeleteByIdsAsync([all[0].Id]);

        Assert.Equal(1, await _repository.CountAsync(null));
    }

    [Fact]
    public async Task GetAtOffsetAsync_按主键顺序定位()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg"),
            CreateItem("D:\\Lib\\c.jpg")
        ]);

        var first = await _repository.GetAtOffsetAsync(null, 0);
        var middle = await _repository.GetAtOffsetAsync(null, 1);

        Assert.NotNull(first);
        Assert.NotNull(middle);
        Assert.NotEqual(first.Id, middle.Id);
    }

    [Fact]
    public async Task GetAtOffsetAsync_偏移量越界_返回空()
    {
        await _repository.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        Assert.Null(await _repository.GetAtOffsetAsync(null, 999));
    }

    [Fact]
    public async Task GetAtOffsetAsync_按类型过滤_仅统计该类型()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg", MediaKind.Image),
            CreateItem("D:\\Lib\\b.mp4", MediaKind.Video)
        ]);

        var video = await _repository.GetAtOffsetAsync(MediaKind.Video, 0);

        Assert.NotNull(video);
        Assert.Equal(MediaKind.Video, video.Kind);
    }

    [Fact]
    public async Task GetAtOffsetAsync_负偏移量_抛出越界异常()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.GetAtOffsetAsync(null, -1));
    }

    [Fact]
    public async Task GetAtOffsetAsync_遍历全部偏移量_不遗漏不重复()
    {
        await _repository.UpsertBatchAsync(Enumerable.Range(0, 60)
            .Select(i => CreateItem($"D:\\Lib\\f{i:D2}.jpg"))
            .ToList());

        var seen = new HashSet<long>();

        for (var offset = 0; offset < 60; offset++)
        {
            var item = await _repository.GetAtOffsetAsync(null, offset);
            Assert.NotNull(item);
            seen.Add(item.Id);
        }

        Assert.Equal(60, seen.Count);
    }

    private MediaItem CreateItem(
        string path,
        MediaKind kind = MediaKind.Image,
        long fileSize = 1024,
        DateTimeOffset? takenAt = null) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
        Kind = kind,
        FileSize = fileSize,
        CreatedUtc = _fixedTime,
        ModifiedUtc = _fixedTime,
        IndexedUtc = _fixedTime,
        TakenUtc = takenAt ?? _fixedTime
    };
}
