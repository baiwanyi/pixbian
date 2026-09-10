/**
 * 媒体条目仓储的集成测试。
 * 职责：针对真实 SQLite 文件验证批量 Upsert、对账查询、删除与计数，并守护「扫描不得覆盖用户数据」这一核心约束。
 * 复用约定：每个用例在临时目录创建独立数据库并在结束时清理；
 *          由 _fixedTime 常量写入固定时间戳保证可重复——仓储自身不接受 TimeProvider，
 *          时间戳一律由调用方显式提供。
 * 关键约束：必须保留「重复 Upsert 不覆盖收藏」用例，该行为一旦回归将直接导致用户收藏丢失。
 */

using Microsoft.Data.Sqlite;
using Pixbian.Core.Models;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>SqliteMediaItemRepository 集成测试。</summary>
public sealed class SqliteMediaItemRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteMediaItemRepository _repository;
    private readonly DateTimeOffset _fixedTime =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>创建临时数据库并完成迁移。</summary>
    public SqliteMediaItemRepositoryTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "Pixbian.Tests",
            $"{Guid.NewGuid():N}.db");

        _initializer = new SqliteDatabaseInitializer(_databasePath);
        _initializer.Initialize();
        _repository = new SqliteMediaItemRepository(_initializer.ConnectionString);
    }

    /// <summary>删除临时数据库及其 WAL 附属文件。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

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
    public async Task UpsertBatchAsync_首次写入_条目入库()
    {
        var items = new[]
        {
            CreateItem("D:\\Lib\\a.jpg", MediaKind.Image),
            CreateItem("D:\\Lib\\sub\\b.mp4", MediaKind.Video)
        };

        await _repository.UpsertBatchAsync(items);

        Assert.Equal(2, await _repository.CountAsync(null));
        Assert.Equal(1, await _repository.CountAsync(MediaKind.Image));
        Assert.Equal(1, await _repository.CountAsync(MediaKind.Video));
    }

    [Fact]
    public async Task UpsertBatchAsync_同路径重复写入_不产生重复记录()
    {
        var first = CreateItem("D:\\Lib\\a.jpg");
        var second = CreateItem("D:\\Lib\\a.jpg");
        second = second with { FileSize = 9999, ModifiedUtc = _fixedTime.AddDays(1) };

        await _repository.UpsertBatchAsync([first]);
        await _repository.UpsertBatchAsync([second]);

        Assert.Equal(1, await _repository.CountAsync(null));
    }

    [Fact]
    public async Task UpsertBatchAsync_重复写入_不覆盖用户收藏与评分()
    {
        var item = CreateItem("D:\\Lib\\a.jpg");
        await _repository.UpsertBatchAsync([item]);

        await MarkAsFavorite("D:\\Lib\\a.jpg");

        var rescanned = CreateItem("D:\\Lib\\a.jpg") with { FileSize = 1 };
        await _repository.UpsertBatchAsync([rescanned]);

        using var connection = new SqliteConnection(_initializer.ConnectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_favorite, rating FROM media_items WHERE path = @path;";
        command.Parameters.AddWithValue("@path", "D:\\Lib\\a.jpg");

        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(5, reader.GetInt32(1));
    }

    [Fact]
    public async Task UpsertBatchAsync_空集合_直接返回不报错()
    {
        await _repository.UpsertBatchAsync([]);

        Assert.Equal(0, await _repository.CountAsync(null));
    }

    [Fact]
    public async Task UpsertBatchAsync_超过单批上限_全部写入()
    {
        var items = Enumerable.Range(0, 850)
            .Select(i => CreateItem($"D:\\Lib\\file{i}.jpg"))
            .ToList();

        await _repository.UpsertBatchAsync(items);

        Assert.Equal(850, await _repository.CountAsync(null));
    }

    [Fact]
    public async Task GetPathsUnderDirectoryAsync_包含子目录条目()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\sub\\b.jpg"),
            CreateItem("D:\\Other\\c.jpg")
        ]);

        var paths = await _repository.GetPathsUnderDirectoryAsync("D:\\Lib");

        Assert.Equal(2, paths.Count);
        Assert.Contains("D:\\Lib\\a.jpg", paths);
        Assert.Contains("D:\\Lib\\sub\\b.jpg", paths);
    }

    [Fact]
    public async Task GetPathsUnderDirectoryAsync_目录名含通配符_不发生误匹配()
    {
        // 目录名含 % 与 _，若未转义会误命中 LibX 等同级目录。
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Li%_b\\a.jpg"),
            CreateItem("D:\\LiXb\\b.jpg"),
            CreateItem("D:\\Li_b\\c.jpg")
        ]);

        var paths = await _repository.GetPathsUnderDirectoryAsync("D:\\Li%_b");

        Assert.Single(paths);
        Assert.Equal("D:\\Li%_b\\a.jpg", paths[0]);
    }

    [Fact]
    public async Task GetPathsUnderDirectoryAsync_前缀相似的兄弟目录_不发生误匹配()
    {
        // 前缀区间改写的边界：'D:\Lib' 不得匹配 'D:\Lib2'、'D:\LibX' 这类同前缀兄弟目录，
        // 也不得因分隔符比较方向写反而把自身目录下的条目漏掉。
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\sub\\b.jpg"),
            CreateItem("D:\\Lib2\\c.jpg"),
            CreateItem("D:\\LibX\\d.jpg"),
            CreateItem("D:\\Li\\e.jpg")
        ]);

        var paths = await _repository.GetPathsUnderDirectoryAsync("D:\\Lib");

        Assert.Equal(2, paths.Count);
        Assert.Contains("D:\\Lib\\a.jpg", paths);
        Assert.Contains("D:\\Lib\\sub\\b.jpg", paths);
    }

    [Fact]
    public async Task 迁移v8_创建分页与计数所需索引()
    {
        // 索引存在性是「查询不走全表」的必要条件；查询计划的选择依赖统计信息，
        // 小数据量下优化器可能仍选全表扫，故这里断言结构而非计划文本。
        using var connection = new SqliteConnection(_initializer.ConnectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_media_items%';";

        var names = new List<string>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Contains("ix_media_items_modified_id", names);
        Assert.Contains("ix_media_items_size_id", names);
        Assert.Contains("ix_media_items_name_id", names);
        Assert.Contains("ix_media_items_kind_deleted", names);
        Assert.Contains("ix_media_items_directory_path", names);
    }

    [Fact]
    public async Task QueryAsync_目录过滤_命中自身与子目录条目()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\sub\\b.jpg"),
            CreateItem("D:\\Other\\c.jpg")
        ]);

        var query = new MediaQuery
        {
            DirectoryPath = "D:\\Lib",
            SortKey = MediaSortKey.FileName,
            SortDirection = SortDirection.Ascending
        };

        var page = await _repository.QueryAsync(query);

        Assert.Equal(2, page.Count);
        Assert.All(page, item => Assert.StartsWith("D:\\Lib\\", item.Path));

        // 列表与页头统计同源：同一过滤条件下两者必须一致。
        Assert.Equal(page.Count, await _repository.CountByQueryAsync(query));
    }

    [Fact]
    public async Task QueryAsync_目录名含通配符_不发生误匹配()
    {
        // 与 GetPathsUnderDirectoryAsync 同一约定：目录名含 % 与 _ 时必须转义后前缀匹配，
        // 否则会误命中 LiXb、Li_b 等同级目录。
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Li%_b\\a.jpg"),
            CreateItem("D:\\LiXb\\b.jpg"),
            CreateItem("D:\\Li_b\\c.jpg")
        ]);

        var page = await _repository.QueryAsync(new MediaQuery { DirectoryPath = "D:\\Li%_b" });

        Assert.Single(page);
        Assert.Equal("D:\\Li%_b\\a.jpg", page[0].Path);
    }

    [Fact]
    public async Task DeleteByPathsAsync_按路径删除()
    {
        await _repository.UpsertBatchAsync([
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

        await _repository.DeleteByPathsAsync(["D:\\Lib\\a.jpg"]);

        Assert.Equal(1, await _repository.CountAsync(null));
    }

    [Fact]
    public async Task DeleteByPathsAsync_超过单批上限_全部删除()
    {
        var paths = Enumerable.Range(0, 900)
            .Select(i => $"D:\\Lib\\file{i}.jpg")
            .ToList();

        await _repository.UpsertBatchAsync(paths.Select(p => CreateItem(p)).ToList());
        await _repository.DeleteByPathsAsync(paths);

        Assert.Equal(0, await _repository.CountAsync(null));
    }

    private MediaItem CreateItem(string path, MediaKind kind = MediaKind.Image) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
        Kind = kind,
        FileSize = 1024,
        CreatedUtc = _fixedTime,
        ModifiedUtc = _fixedTime,
        IndexedUtc = _fixedTime,
        TakenUtc = _fixedTime
    };

    /// <summary>直接写库模拟用户在界面上的收藏与评分操作。</summary>
    private async Task MarkAsFavorite(string path)
    {
        using var connection = new SqliteConnection(_initializer.ConnectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE media_items SET is_favorite = 1, rating = 5 WHERE path = @path;
            """;
        command.Parameters.AddWithValue("@path", path);

        await command.ExecuteNonQueryAsync();
    }
}
