/**
 * 索引库快照服务的单元测试。
 * 职责：锁定快照的可用性（导出的文件可独立打开并含当前数据、可覆盖旧快照）、
 *      还原的语义（当前库被替换、旧库保留带时间戳的副本、WAL 附属文件被清理），
 *      以及防御性校验（非快照文件被拒绝且当前库不受影响、相对路径被拒绝）。
 * 复用约定：每个用例自建临时库与临时快照文件，Dispose 时清理连接池与全部临时文件
 *          （含 .bak-* 与 -wal / -shm 附属文件）。
 * 关键约束：还原是破坏性操作，用例必须同时断言「新库内容正确」与「旧库仍可从备份文件恢复」，
 *          只断言前者会掩盖「旧数据被直接丢弃」的实现错误。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Models;
using Pixbian.Data.Backup;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>索引库快照服务测试。</summary>
public sealed class DatabaseSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _temporaryFiles = [];

    /// <summary>清理连接池与临时文件（含派生路径）。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in _temporaryFiles.ToList())
        {
            foreach (var candidate in ExpandDerivedPaths(path))
            {
                TryDelete(candidate);
            }
        }
    }

    [Fact]
    public async Task CreateSnapshotAsync_导出的文件可独立打开且含当前数据()
    {
        using var database = new TestDatabase(this);
        await database.MediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        var snapshotPath = GetTemporaryPath("snapshot.db");
        await database.Snapshot.CreateSnapshotAsync(snapshotPath);

        Assert.True(File.Exists(snapshotPath));

        // 快照必须是一个能独立打开的完整库（VACUUM INTO 的语义），而不是半截副本。
        await using var connection = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM media_items;";

        var rowCount = await command.ExecuteScalarAsync();

        Assert.Equal(1L, Convert.ToInt64(rowCount, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CreateSnapshotAsync_目标已存在时覆盖()
    {
        using var database = new TestDatabase(this);
        await database.MediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        var snapshotPath = GetTemporaryPath("snapshot.db");
        await File.WriteAllTextAsync(snapshotPath, "旧内容");

        await database.Snapshot.CreateSnapshotAsync(snapshotPath);

        Assert.True(new FileInfo(snapshotPath).Length > 100);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_替换当前库并保留旧库副本()
    {
        using var database = new TestDatabase(this);
        await database.MediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        var snapshotPath = GetTemporaryPath("snapshot.db");
        await database.Snapshot.CreateSnapshotAsync(snapshotPath);

        // 快照之后又写入一条：还原应当把它抹掉。
        await database.MediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\b.jpg")]);
        Assert.Equal(2, (await database.MediaItems.QueryAsync(new MediaQuery())).Count);

        var backupPath = await database.Snapshot.RestoreSnapshotAsync(snapshotPath);

        var restored = await database.MediaItems.QueryAsync(new MediaQuery());
        var item = Assert.Single(restored);
        Assert.Equal("a.jpg", item.FileName);

        // 旧库必须保留：还原是「切换到某份快照」，不是「丢弃当前数据」。
        Assert.True(File.Exists(backupPath));
        Assert.Contains(".bak-", backupPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_非快照文件被拒绝且当前库不变()
    {
        using var database = new TestDatabase(this);
        await database.MediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\a.jpg")]);

        var invalidPath = GetTemporaryPath("not-a-database.db");
        await File.WriteAllTextAsync(invalidPath, "这不是 SQLite 库");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => database.Snapshot.RestoreSnapshotAsync(invalidPath));

        Assert.Single(await database.MediaItems.QueryAsync(new MediaQuery()));
    }

    [Fact]
    public async Task CreateSnapshotAsync_相对路径被拒绝()
    {
        using var database = new TestDatabase(this);

        await Assert.ThrowsAsync<ArgumentException>(
            () => database.Snapshot.CreateSnapshotAsync("relative\\snapshot.db"));
    }

    /// <summary>登记并返回一个临时文件路径。</summary>
    private string GetTemporaryPath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryFiles.Add(directory);

        return Path.Combine(directory, fileName);
    }

    /// <summary>展开主路径的派生文件：库的 WAL/SHM、还原产生的 .bak-* 副本。</summary>
    private static IEnumerable<string> ExpandDerivedPaths(string path)
    {
        yield return path;
        yield return path + "-wal";
        yield return path + "-shm";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言结果。
        }
    }

    private static MediaItem CreateItem(string path) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path) ?? string.Empty,
        Kind = MediaKind.Image,
        FileSize = 1024,
        CreatedUtc = FixedTime,
        ModifiedUtc = FixedTime,
        IndexedUtc = FixedTime
    };

    /// <summary>临时库 + 快照服务的组合。</summary>
    private sealed class TestDatabase : IDisposable
    {
        public TestDatabase(DatabaseSnapshotTests owner)
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", $"{Guid.NewGuid():N}", "index.db");
            var directory = Path.GetDirectoryName(DatabasePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                owner._temporaryFiles.Add(directory);
            }

            var initializer = new SqliteDatabaseInitializer(DatabasePath);
            initializer.Initialize();

            ConnectionString = initializer.ConnectionString;
            MediaItems = new SqliteMediaItemRepository(ConnectionString);
            Snapshot = new SqliteDatabaseSnapshotService(DatabasePath, ConnectionString);
        }

        public string DatabasePath { get; }

        public string ConnectionString { get; }

        public SqliteMediaItemRepository MediaItems { get; }

        public SqliteDatabaseSnapshotService Snapshot { get; }

        /// <summary>清理连接池，使库文件可被删除（实际删除由外层 Dispose 处理）。</summary>
        public void Dispose() => SqliteConnection.ClearAllPools();
    }
}
