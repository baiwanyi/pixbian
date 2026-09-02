/**
 * 缩略图磁盘缓存集成测试。
 * 职责：验证条目的读写闭环、源文件指纹失效、Invalidate、LRU 容量驱逐与重启恢复。
 * 复用约定：与仓储测试共用约定——独立临时目录、真实文件系统、xUnit 每实例独立 fixture。
 * 关键约束：驱逐用例依赖两条目访问序可区分，写入之间必须显式间隔（UtcNow 精度可达同一 tick）；
 *          指纹校验直接操作源文件 mtime，模拟用户编辑媒体文件后的缓存失效。
 */

using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>缩略图磁盘缓存集成测试。</summary>
public sealed class ThumbnailDiskCacheTests : IDisposable
{
    private readonly string _directory;
    private readonly string _sourcePath;
    private readonly byte[] _payload = Convert.FromHexString("DEADBEEF0102030405060708090A0B0C0D0E0F10");

    /// <summary>创建独立的缓存目录与源媒体文件。</summary>
    public ThumbnailDiskCacheTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", "Thumbs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _sourcePath = Path.Combine(_directory, "sample.jpg");
        File.WriteAllBytes(_sourcePath, new byte[256]);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 文件句柄尚未释放时放弃清理，残留目录由系统临时目录策略兜底。
        }
    }

    private ThumbnailDiskCache CreateCache(long capacityBytes = 2L * 1024 * 1024 * 1024) =>
        new(_directory, capacityBytes);

    /// <summary>递归收集全部条目文件：分桶后条目位于哈希子目录内。</summary>
    private string[] EnumerateEntries() =>
        Directory.GetFiles(_directory, "*.px", SearchOption.AllDirectories);

    private async Task<ThumbnailDiskCache> CreateFilledCacheAsync()
    {
        var cache = CreateCache();
        await cache.StoreAsync(_sourcePath, 256, _payload);

        return cache;
    }

    [Fact]
    public async Task StoreAsync_后_TryGetAsync_命中且载荷一致()
    {
        var cache = await CreateFilledCacheAsync();

        var bytes = await cache.TryGetAsync(_sourcePath, 256);

        Assert.NotNull(bytes);
        Assert.Equal(_payload, bytes);
    }

    [Fact]
    public async Task 未写入的档位_未命中()
    {
        var cache = await CreateFilledCacheAsync();

        Assert.Null(await cache.TryGetAsync(_sourcePath, 512));
    }

    [Fact]
    public async Task 源文件被修改后_TryGetAsync_未命中且条目被清除()
    {
        var cache = await CreateFilledCacheAsync();

        // 模拟用户编辑媒体文件：mtime 变化使指纹失配。
        File.SetLastWriteTimeUtc(_sourcePath, File.GetLastWriteTimeUtc(_sourcePath).AddMinutes(5));

        Assert.Null(await cache.TryGetAsync(_sourcePath, 256));

        // 失效条目应被即时删除，而非等待 LRU 驱逐。
        Assert.Empty(EnumerateEntries());
    }

    [Fact]
    public async Task 源文件被删除后_TryGetAsync_未命中()
    {
        var cache = await CreateFilledCacheAsync();
        File.Delete(_sourcePath);

        Assert.Null(await cache.TryGetAsync(_sourcePath, 256));
    }

    [Fact]
    public async Task Invalidate_后_未命中且磁盘条目被删除()
    {
        var cache = await CreateFilledCacheAsync();

        cache.Invalidate(_sourcePath);

        Assert.Null(await cache.TryGetAsync(_sourcePath, 256));
        Assert.Empty(EnumerateEntries());
    }

    [Fact]
    public async Task 条目按哈希两级分桶存放()
    {
        var cache = await CreateFilledCacheAsync();

        // 条目应位于哈希前 4 个十六进制字符的两级子目录中，而非缓存根目录。
        var relative = Path.GetRelativePath(_directory, EnumerateEntries().Single());

        Assert.Equal(3, relative.Split(Path.DirectorySeparatorChar).Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.px"));
    }

    [Fact]
    public async Task 超出容量_驱逐最旧条目()
    {
        // 单条 16 + 20 = 36 字节；容量只容下一条余量，写入第二条必须驱逐最旧。
        var cache = CreateCache(capacityBytes: 46);

        await cache.StoreAsync(_sourcePath, 256, _payload);
        await Task.Delay(20);

        var secondPath = Path.Combine(_directory, "second.jpg");
        File.WriteAllBytes(secondPath, new byte[64]);

        await cache.StoreAsync(secondPath, 256, _payload);

        Assert.Null(await cache.TryGetAsync(_sourcePath, 256));
        Assert.NotNull(await cache.TryGetAsync(secondPath, 256));
    }

    [Fact]
    public async Task 容量未超限_不驱逐()
    {
        var cache = CreateCache(capacityBytes: 1024);

        await cache.StoreAsync(_sourcePath, 256, _payload);

        Assert.NotNull(await cache.TryGetAsync(_sourcePath, 256));
    }

    [Fact]
    public async Task 重建实例后_经初始化恢复命中()
    {
        var first = await CreateFilledCacheAsync();

        // 模拟应用重启：新实例指向同一目录，先扫描再读。
        var second = CreateCache();
        await second.InitializeAsync();

        var bytes = await second.TryGetAsync(_sourcePath, 256);

        Assert.NotNull(bytes);
        Assert.Equal(_payload, bytes);
        GC.KeepAlive(first);
    }

    [Fact]
    public async Task 初始化前_按未命中处理()
    {
        var cache = await CreateFilledCacheAsync();
        var fresh = CreateCache();

        // 新实例尚未 InitializeAsync：表为空，直接 miss（启动期可接受的一次性退化）。
        Assert.Null(await fresh.TryGetAsync(_sourcePath, 256));
    }

    [Fact]
    public async Task 残留零字节条目_初始化时被清除()
    {
        var cache = await CreateFilledCacheAsync();
        var entryFile = EnumerateEntries().Single();
        File.WriteAllText(entryFile, string.Empty);

        var second = CreateCache();
        await second.InitializeAsync();

        Assert.Empty(EnumerateEntries());
    }

    [Fact]
    public async Task StoreAsync_源文件不存在_不写条目()
    {
        var cache = CreateCache();
        var missingPath = Path.Combine(_directory, "missing.jpg");

        await cache.StoreAsync(missingPath, 256, _payload);

        Assert.Empty(EnumerateEntries());
        Assert.Null(await cache.TryGetAsync(missingPath, 256));
    }
}
