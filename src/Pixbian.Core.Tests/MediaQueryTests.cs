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
    public async Task QueryAsync_随机游标_按rank升序翻页且不重复()
    {
        await _repository.UpsertBatchAsync(
        [
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg"),
            CreateItem("D:\\Lib\\c.jpg"),
            CreateItem("D:\\Lib\\d.jpg"),
            CreateItem("D:\\Lib\\e.jpg")
        ]);

        // v4 触发器为每条生成唯一 rank；翻页游标取上一页末条 rank+1，两页之间不得重叠。
        var all = await _repository.QueryAsync(new MediaQuery());
        var ranks = all.Select(i => i.RandomRank!.Value).ToList();
        Assert.Equal(ranks.Count, ranks.Distinct().Count());

        var start = ranks.Order().Skip(2).First();
        var page = await _repository.QueryAsync(
            new MediaQuery { SortKey = MediaSortKey.Random, RandomCursor = start, Take = 10 });

        Assert.NotEmpty(page);
        Assert.All(page, i => Assert.True(i.RandomRank!.Value >= start));
        Assert.Equal(page.Select(i => i.Id), page.OrderBy(i => i.RandomRank).Select(i => i.Id));

        var seenIds = page.Select(i => i.Id).ToHashSet();
        var next = await _repository.QueryAsync(new MediaQuery
        {
            SortKey = MediaSortKey.Random,
            RandomCursor = page[^1].RandomRank!.Value + 1,
            Take = 10
        });

        Assert.All(next, i => Assert.DoesNotContain(i.Id, seenIds));
        Assert.All(next, i => Assert.True(i.RandomRank!.Value > page[^1].RandomRank!.Value));
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
    public async Task QueryAsync_键集分页_三种排序键逐页拼接与OFFSET全量一致()
    {
        // file_size 取 i % 5 制造重复值：验证排序值相等时 id tie-breaker 的正确性。
        await _repository.UpsertBatchAsync(Enumerable.Range(0, 12)
            .Select(i => CreateItem(
                $"D:\\Lib\\file{i:D2}.jpg",
                modifiedAt: _fixedTime.AddDays(i),
                fileSize: i % 5))
            .ToList());

        foreach (var (sortKey, direction, buildCursor) in new[]
                 {
                     (MediaSortKey.ModifiedDate, SortDirection.Descending,
                         (Func<MediaItem, KeysetCursor>)(i => new KeysetCursor(i.Id, LastUtc: i.ModifiedUtc))),
                     (MediaSortKey.FileSize, SortDirection.Ascending,
                         (Func<MediaItem, KeysetCursor>)(i => new KeysetCursor(i.Id, LastNumber: i.FileSize))),
                     (MediaSortKey.FileName, SortDirection.Descending,
                         (Func<MediaItem, KeysetCursor>)(i => new KeysetCursor(i.Id, LastText: i.FileName)))
                 })
        {
            var expected = await _repository.QueryAsync(
                new MediaQuery { SortKey = sortKey, SortDirection = direction, Take = 100 });

            KeysetCursor? cursor = null;
            var collected = new List<MediaItem>();

            while (true)
            {
                var page = await _repository.QueryAsync(new MediaQuery
                {
                    SortKey = sortKey,
                    SortDirection = direction,
                    Take = 5,
                    Keyset = cursor
                });

                if (page.Count == 0)
                {
                    break;
                }

                collected.AddRange(page);

                var last = page[^1];
                cursor = buildCursor(last);
            }

            Assert.Equal(expected.Count, collected.Count);
            Assert.Equal(expected.Select(i => i.Id), collected.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task QueryAsync_键集分页_翻页之间不重复不遗漏()
    {
        await _repository.UpsertBatchAsync(Enumerable.Range(0, 9)
            .Select(i => CreateItem($"D:\\Lib\\page{i:D2}.jpg", fileSize: i))
            .ToList());

        var firstPage = await _repository.QueryAsync(
            new MediaQuery { SortKey = MediaSortKey.FileSize, SortDirection = SortDirection.Ascending, Take = 4 });

        var last = firstPage[^1];
        var secondPage = await _repository.QueryAsync(new MediaQuery
        {
            SortKey = MediaSortKey.FileSize,
            SortDirection = SortDirection.Ascending,
            Take = 4,
            Keyset = new KeysetCursor(last.Id, LastNumber: last.FileSize)
        });

        Assert.Equal(4, secondPage.Count);
        Assert.Empty(firstPage.Select(f => f.Id).Intersect(secondPage.Select(f => f.Id)));

        // 键集比较必须与排序方向一致：升序游标之后的所有条目都严格大于末条。
        Assert.All(secondPage, i => Assert.True(i.FileSize > last.FileSize
                                                || (i.FileSize == last.FileSize && i.Id > last.Id)));
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
            new MediaQuery { SortKey = MediaSortKey.FileName, SortDirection = SortDirection.Ascending });

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
            new MediaQuery { SortKey = MediaSortKey.FileSize, SortDirection = SortDirection.Descending });

        Assert.Equal(["large.jpg", "medium.jpg", "small.jpg"], result.Select(i => i.FileName));
    }

    [Fact]
    public async Task QueryAsync_修改日期排序_按方向返回结果()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\old.jpg", modifiedAt: _fixedTime.AddDays(-1)),
            CreateItem("D:\\Lib\\new.jpg", modifiedAt: _fixedTime.AddDays(1)),
            CreateItem("D:\\Lib\\mid.jpg", modifiedAt: _fixedTime)
        ]);

        var descending = await _repository.QueryAsync(new MediaQuery());
        var ascending = await _repository.QueryAsync(
            new MediaQuery { SortDirection = SortDirection.Ascending });

        Assert.Equal(["new.jpg", "mid.jpg", "old.jpg"], descending.Select(i => i.FileName));
        Assert.Equal(["old.jpg", "mid.jpg", "new.jpg"], ascending.Select(i => i.FileName));
    }

    [Fact]
    public async Task QueryAsync_随机排序_同种子分页不重复()
    {
        await _repository.UpsertBatchAsync(Enumerable.Range(0, 10)
            .Select(i => CreateItem($"D:\\Lib\\file{i:D2}.jpg"))
            .ToList());

        var firstPage = await _repository.QueryAsync(
            new MediaQuery { SortKey = MediaSortKey.Random, RandomSeed = 20260901, Take = 5 });
        var secondPage = await _repository.QueryAsync(
            new MediaQuery { SortKey = MediaSortKey.Random, RandomSeed = 20260901, Skip = 5, Take = 5 });

        Assert.Equal(10, firstPage.Count + secondPage.Count);
        Assert.Empty(firstPage.Select(f => f.Path).Intersect(secondPage.Select(f => f.Path)));
    }

    [Fact]
    public async Task CountByQueryAsync_按类型与搜索条件计数_与QueryAsync同源()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\IMG_0001.jpg"),
            CreateItem("D:\\Lib\\IMG_0002.jpg"),
            CreateItem("D:\\Lib\\clip.mp4", MediaKind.Video),
            CreateItem("D:\\Lib\\screenshot.png")
        ]);

        var query = new MediaQuery { SearchText = "IMG_" };

        var count = await _repository.CountByQueryAsync(query);
        var listed = await _repository.QueryAsync(query);

        Assert.Equal(2, count);
        Assert.Equal(listed.Count, count);
    }

    [Fact]
    public async Task CountByQueryAsync_按收藏条件计数_仅统计收藏条目()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        var all = await _repository.QueryAsync(new MediaQuery());
        await _repository.SetFavoriteAsync([all[0].Id], true);

        Assert.Equal(1, await _repository.CountByQueryAsync(new MediaQuery { IsFavorite = true }));
        Assert.Equal(1, await _repository.CountByQueryAsync(new MediaQuery { IsFavorite = false }));
    }

    [Fact]
    public async Task CountByQueryAsync_分页字段不参与计数()
    {
        await _repository.UpsertBatchAsync(Enumerable.Range(0, 10)
            .Select(i => CreateItem($"D:\\Lib\\file{i:D2}.jpg"))
            .ToList());

        var total = await _repository.CountByQueryAsync(new MediaQuery());
        var paged = await _repository.CountByQueryAsync(new MediaQuery { Skip = 0, Take = 3 });

        Assert.Equal(10, total);
        Assert.Equal(total, paged);
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
        DateTimeOffset? takenAt = null,
        DateTimeOffset? modifiedAt = null) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
        Kind = kind,
        FileSize = fileSize,
        CreatedUtc = _fixedTime,
        ModifiedUtc = modifiedAt ?? _fixedTime,
        IndexedUtc = _fixedTime,
        TakenUtc = takenAt ?? _fixedTime
    };
}
