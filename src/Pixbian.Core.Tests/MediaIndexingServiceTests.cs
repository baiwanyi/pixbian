/**
 * 媒体索引服务的集成测试。
 * 职责：在真实临时目录中创建文件，验证扫描入库、格式过滤、对账移除与进度上报。
 * 复用约定：使用真实的 SQLite 仓储与真实文件系统，避免桩实现掩盖路径处理与事务行为上的缺陷；
 *          每个用例使用独立临时目录，结束后递归清理。
 * 关键约束：对账用例必须验证「文件删除后索引条目被移除」，这是防止僵尸记录累积的唯一防线；
 *          同时必须验证「存在不可访问目录时放弃对账」，这是防止误删现存条目的唯一防线——
 *          不可访问目录的构造依赖文件系统权限且受是否管理员影响，故经桩枚举器注入计数，保证稳定可复现；
 *          符号链接相关行为依赖具体文件系统权限，此处不做断言，仅保证代码路径不抛异常。
 */

using Microsoft.Data.Sqlite;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Core.Utilities;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>MediaIndexingService 集成测试。</summary>
public sealed class MediaIndexingServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteMediaItemRepository _mediaItems;
    private readonly SqliteLibraryFolderRepository _libraryFolders;

    /// <summary>创建临时扫描目录与临时数据库。</summary>
    public MediaIndexingServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _databasePath = Path.Combine(_root, "index.db");
        _initializer = new SqliteDatabaseInitializer(_databasePath);
        _initializer.Initialize();

        _mediaItems = new SqliteMediaItemRepository(_initializer.ConnectionString);
        _libraryFolders = new SqliteLibraryFolderRepository(_initializer.ConnectionString);
    }

    /// <summary>清理临时目录与数据库连接池。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_目录含图片与视频_全部入库()
    {
        await CreateFileAsync("a.jpg");
        await CreateFileAsync("b.png");
        await CreateFileAsync("c.mp4");

        var report = await ScanAsync();

        Assert.Equal(3, report.IndexedCount);
        Assert.Equal(0, report.RemovedCount);
        Assert.Equal(3, await _mediaItems.CountAsync(null));
        Assert.Equal(2, await _mediaItems.CountAsync(MediaKind.Image));
        Assert.Equal(1, await _mediaItems.CountAsync(MediaKind.Video));
    }

    [Fact]
    public async Task ScanAsync_含不支持格式_仅入库受支持文件()
    {
        await CreateFileAsync("a.jpg");
        await CreateFileAsync("notes.txt");
        await CreateFileAsync("data.json");

        var report = await ScanAsync();

        Assert.Equal(1, report.IndexedCount);
        Assert.Equal(1, await _mediaItems.CountAsync(null));
    }

    [Fact]
    public async Task ScanAsync_遍历子目录_递归入库()
    {
        Directory.CreateDirectory(Path.Combine(_root, "2026", "08"));
        await CreateFileAsync(Path.Combine("2026", "08", "deep.jpg"));

        var report = await ScanAsync();

        Assert.Equal(1, report.IndexedCount);
    }

    [Fact]
    public async Task ScanAsync_文件被删除_对账后移除索引()
    {
        var filePath = await CreateFileAsync("a.jpg");
        await ScanAsync();
        Assert.Equal(1, await _mediaItems.CountAsync(null));

        File.Delete(filePath);
        var report = await ScanAsync();

        Assert.Equal(0, report.IndexedCount);
        Assert.Equal(1, report.RemovedCount);
        Assert.Equal(0, await _mediaItems.CountAsync(null));
    }

    [Fact]
    public async Task ScanAsync_存在不可访问目录_放弃对账并保留既有条目()
    {
        var first = await CreateFileAsync("a.jpg");
        await CreateFileAsync("b.jpg");
        await ScanAsync();
        Assert.Equal(2, await _mediaItems.CountAsync(null));

        // 模拟「b.jpg 所在子树本次不可访问」：它不会出现在本轮发现集合中。
        var report = await ScanWithStubAsync([first], inaccessibleDirectories: 1);

        Assert.Equal(1, report.IndexedCount);
        Assert.Equal(1, report.InaccessibleDirectoryCount);
        Assert.True(report.ReconcileSkipped);

        // 关键断言：不可访问目录存续期间，任何条目都不得被删除。
        Assert.Equal(0, report.RemovedCount);
        Assert.Equal(2, await _mediaItems.CountAsync(null));
    }

    [Fact]
    public async Task ScanAsync_无不可访问目录_对账照常删除失效条目()
    {
        var first = await CreateFileAsync("a.jpg");
        var second = await CreateFileAsync("b.jpg");
        await ScanAsync();

        File.Delete(second);

        var report = await ScanWithStubAsync([first], inaccessibleDirectories: 0);

        Assert.Equal(0, report.InaccessibleDirectoryCount);
        Assert.False(report.ReconcileSkipped);
        Assert.Equal(1, report.RemovedCount);
        Assert.Equal(1, await _mediaItems.CountAsync(null));
    }

    [Fact]
    public async Task ScanAsync_不可访问目录恢复后_下一轮对账清理生效()
    {
        var first = await CreateFileAsync("a.jpg");
        var second = await CreateFileAsync("b.jpg");
        await ScanAsync();

        File.Delete(second);

        // 首轮子目录不可访问：跳过对账。
        await ScanWithStubAsync([first], inaccessibleDirectories: 1);
        Assert.Equal(2, await _mediaItems.CountAsync(null));

        // 次轮访问恢复：失效条目被正常清理，说明跳过只是延后而非永久残留。
        var report = await ScanWithStubAsync([first], inaccessibleDirectories: 0);

        Assert.Equal(1, report.RemovedCount);
        Assert.Equal(1, await _mediaItems.CountAsync(null));
    }

    [Fact]
    public async Task ScanAsync_重复扫描_不产生重复条目()
    {
        await CreateFileAsync("a.jpg");

        await ScanAsync();
        await ScanAsync();

        Assert.Equal(1, await _mediaItems.CountAsync(null));
    }

    [Fact]
    public async Task ScanAsync_上报进度_累计值递增()
    {
        await CreateFileAsync("a.jpg");
        await CreateFileAsync("b.jpg");
        await CreateFileAsync("c.jpg");

        var reported = new List<int>();

        // 不使用 Progress<T>：它通过线程池异步投递回调，断言时回调可能尚未执行，导致用例随机失败。
        await ScanAsync(new SynchronousProgress<IndexingProgress>(p => reported.Add(p.ProcessedCount)));

        Assert.NotEmpty(reported);
        Assert.Equal(3, reported[^1]);
        Assert.True(reported.SequenceEqual(reported.OrderBy(x => x)), "进度上报值应单调递增。");
    }

    [Fact]
    public async Task ScanAsync_隐藏文件_不入库()
    {
        var path = await CreateFileAsync("hidden.jpg");
        File.SetAttributes(path, FileAttributes.Hidden);

        var report = await ScanAsync();

        Assert.Equal(0, report.IndexedCount);

        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public async Task ScanAsync_更新扫描源的最后扫描时间()
    {
        await CreateFileAsync("a.jpg");
        var folder = await _libraryFolders.AddAsync(_root);

        var service = new MediaIndexingService(_mediaItems, _libraryFolders);
        await service.ScanAsync(folder);

        var folders = await _libraryFolders.GetAllAsync();
        Assert.NotNull(folders[0].LastScanUtc);
    }

    [Fact]
    public async Task ScanAsync_目录不存在_返回空报告而不抛异常()
    {
        var missing = Path.Combine(_root, "not-created");
        var folder = new LibraryFolder { Id = 1, Path = missing };

        var service = new MediaIndexingService(_mediaItems, _libraryFolders);
        var report = await service.ScanAsync(folder);

        Assert.Equal(0, report.IndexedCount);
        Assert.Equal(0, report.RemovedCount);
    }

    [Fact]
    public async Task ScanAsync_取消令牌已取消_抛出操作取消异常()
    {
        await CreateFileAsync("a.jpg");
        var folder = await _libraryFolders.AddAsync(_root);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var service = new MediaIndexingService(_mediaItems, _libraryFolders);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ScanAsync(folder, null, cts.Token));
    }

    /// <summary>在扫描目录下创建文件并返回其完整路径。</summary>
    private async Task<string> CreateFileAsync(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, "test");
        return fullPath;
    }

    /// <summary>对临时目录执行一次扫描。</summary>
    private async Task<IndexingReport> ScanAsync(IProgress<IndexingProgress>? progress = null)
    {
        var folder = await _libraryFolders.AddAsync(_root);
        var service = new MediaIndexingService(_mediaItems, _libraryFolders);

        return await service.ScanAsync(folder, progress);
    }

    /// <summary>用桩枚举器执行一次扫描：可精确控制发现集合与不可访问目录计数。</summary>
    private async Task<IndexingReport> ScanWithStubAsync(
        IReadOnlyList<string> discoveredFiles,
        int inaccessibleDirectories)
    {
        var folder = await _libraryFolders.AddAsync(_root);

        var service = new MediaIndexingService(
            _mediaItems,
            _libraryFolders,
            fileEnumerator: new StubMediaFileEnumerator(discoveredFiles, inaccessibleDirectories));

        return await service.ScanAsync(folder);
    }

    /// <summary>桩枚举器：按预设返回文件并登记不可访问目录数，规避对文件系统权限的依赖。</summary>
    private sealed class StubMediaFileEnumerator(
        IReadOnlyList<string> files,
        int inaccessibleDirectories) : IMediaFileEnumerator
    {
        public IEnumerable<FileInfo> Enumerate(
            string rootDirectory,
            InaccessibleDirectoryCounter counter)
        {
            ArgumentNullException.ThrowIfNull(counter);

            for (var i = 0; i < inaccessibleDirectories; i++)
            {
                counter.Record();
            }

            foreach (var file in files)
            {
                yield return new FileInfo(file);
            }
        }
    }

    /// <summary>同步执行回调的进度上报器，用于消除测试中的时序不确定性。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
