/**
 * 缩略图磁盘缓存（M3 阶段 C）。
 * 职责：把编码后的缩略图字节持久化到本地目录，按 LRU 容量（默认 2 GB）驱逐最旧条目，
 *       让二次浏览与重启后的首屏加载跳过「读原图 → 解码 → 重采样 → 编码」全链路。
 * 复用约定：条目由 ThumbnailService 在编码管线内顺手写入；读取端只认本类定义的条目格式；
 *          缓存目录统一取 AppPaths.ThumbnailCacheDirectory，禁止另立目录。
 * 关键约束：本类是纯字节 IO 设施，不触碰任何 WinUI 类型，放 Core 以便单元测试；
 *          条目为「两级哈希分桶目录 + 档位文件名」，杜绝原路径中的非法字符与注入面：
 *          按路径哈希前 4 个十六进制字符分两级（256 × 256 = 65536 桶），
 *          按百万级条目管理——每桶平均 15~30 条，目录枚举与整体清理都在小粒度上完成。
 *          分桶键与文件名共用同一哈希：同一文件的全部档位聚在同一桶内，
 *          删除与扫描的局部性好。代价：初始化扫描要遍历 65536 个目录（后台任务，可接受），
 *          LRU 内存表在百万级条目下约占 150MB，规模继续上涨需改紧凑存储（已知项）。
 *          条目头携带源文件 mtime + 大小指纹，源文件被编辑后旧缩略图自动失效；
 *          全部失败（磁盘满、权限、文件被占用）一律静默——磁盘缓存是加速层，
 *          任何故障都不允许影响主解码路径，最多退化为重新编码。
 *          LRU 访问序持久化依赖文件的 LastWriteTimeUtc（命中时节流 touch），
 *          重启后经 InitializeAsync 扫描重建，扫描完成前 TryGetAsync 按 miss 处理，
 *          首屏多走一次全量编码，属可接受的启动期退化。
 */

using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>缩略图磁盘缓存。</summary>
public interface IThumbnailDiskCache
{
    /// <summary>扫描缓存目录重建 LRU 表；应用启动时调用一次，可重复调用。</summary>
    /// <returns>扫描登记的条目数；调用方可据此确认扫描是否生效。</returns>
    Task<int> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>读取条目；未命中、源文件已变化或条目损坏时返回 null（并清理失效条目）。</summary>
    /// <param name="path">源媒体文件完整路径。</param>
    /// <param name="bucket">解码档位（物理像素最长边）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<byte[]?> TryGetAsync(string path, int bucket, CancellationToken cancellationToken = default);

    /// <summary>写入条目；内部含原子替换与容量驱逐，调用方可 await（测试）或弃任务（生产热路径）。</summary>
    /// <param name="path">源媒体文件完整路径。</param>
    /// <param name="bucket">解码档位（物理像素最长边）。</param>
    /// <param name="payload">编码后的图像字节（不含指纹头，由本类负责添加）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StoreAsync(string path, int bucket, byte[] payload, CancellationToken cancellationToken = default);

    /// <summary>删除指定源文件在全部档位上的条目。</summary>
    /// <param name="path">源媒体文件完整路径。</param>
    void Invalidate(string path);
}

/// <summary>基于文件系统目录的 LRU 磁盘缓存实现。</summary>
public sealed class ThumbnailDiskCache : IThumbnailDiskCache
{
    /// <summary>条目指纹头长度：源文件 mtime（8 字节）+ 源文件大小（8 字节）。</summary>
    private const int HeaderLength = 16;

    /// <summary>路径哈希截取的字节数（12 个十六进制字符）：96 位空间，冲突概率可忽略。</summary>
    private const int HashBytes = 6;

    /// <summary>LRU 容量。</summary>
    private readonly long _capacityBytes;

    /// <summary>驱逐目标：超限后裁剪到容量的 90%，避免每次写入都触发驱逐。</summary>
    private const double EvictionTargetRatio = 0.9;

    /// <summary>命中 touch 节流阈值：磁盘 LastWriteTime 仅作为跨会话访问序代理，
    /// 内存表每次命中都更新，磁盘只在间隔超过该值时回写，避免滚动浏览时的小 IO 洪峰。</summary>
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(60);

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _evictionGate = new();
    private long _totalBytes;

    /// <summary>条目元数据。</summary>
    /// <param name="Size">条目文件字节数（含指纹头）。</param>
    /// <param name="LastAccess">最近访问时间；启动期取自文件 LastWriteTimeUtc。</param>
    private sealed record Entry(long Size, DateTimeOffset LastAccess)
    {
        public Entry Touch() => this with { LastAccess = DateTimeOffset.UtcNow };
    }

    /// <summary>初始化磁盘缓存。</summary>
    /// <param name="directory">条目存放目录。</param>
    /// <param name="capacityBytes">LRU 容量（字节）。</param>
    public ThumbnailDiskCache(string directory, long capacityBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, HeaderLength);

        _directory = directory;
        _capacityBytes = capacityBytes;
    }

    /// <inheritdoc />
    public Task<int> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                Directory.CreateDirectory(_directory);

                // 哈希分桶后条目分散在 256 个子目录，递归枚举一次性重建。
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };

                foreach (var file in Directory.EnumerateFiles(_directory, "*.px", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var info = new FileInfo(file);

                        // 0 字节条目是上次写入中断的残骸，直接清掉。
                        if (info.Length < HeaderLength)
                        {
                            info.Delete();
                            continue;
                        }

                        // LastWriteTimeUtc 由命中时的节流 touch 维护，重启后即恢复访问序。
                        _entries[Path.GetRelativePath(_directory, file)] = new Entry(info.Length, info.LastWriteTimeUtc);
                        _totalBytes += info.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件扫描失败不影响其余条目。
                    }
                }

                return _entries.Count;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]?> TryGetAsync(string path, int bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var entryPath = GetEntryRelativePath(path, bucket);

        if (!_entries.TryGetValue(entryPath, out _))
        {
            return null;
        }

        byte[] bytes;

        try
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(_directory, entryPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到（被占用 / 已被驱逐）按 miss 处理，走主解码路径。
            return null;
        }

        if (bytes.Length < HeaderLength)
        {
            Discard(entryPath);
            return null;
        }

        if (!IsSourceUnchanged(path, bytes))
        {
            // 源文件已编辑：旧缩略图不再可信，删除后由调用方重新编码写入。
            Discard(entryPath);
            return null;
        }

        RecordHit(entryPath);
        return bytes[HeaderLength..];
    }

    /// <inheritdoc />
    public async Task StoreAsync(string path, int bucket, byte[] payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (payload.Length == 0)
        {
            return;
        }

        FileInfo? source;
        try
        {
            source = new FileInfo(path);

            if (!source.Exists)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var entryPath = GetEntryRelativePath(path, bucket);
        var entrySize = HeaderLength + payload.Length;

        try
        {
            // temp + 原子替换：并发写入同一档位时不会让读者看到半截文件。
            // 分桶子目录随首个条目懒创建。
            var tempPath = Path.Combine(_directory, entryPath + ".tmp");
            var targetPath = Path.Combine(_directory, entryPath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteHeader(stream, source);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 磁盘满 / 权限 / 目标被占用（驱逐删除与并发读共享句柄）都只放弃本次写入。
            return;
        }

        Upsert(entryPath, entrySize);
    }

    /// <inheritdoc />
    public void Invalidate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        foreach (var bucket in ThumbnailSizes.DecodeBuckets)
        {
            var entryPath = GetEntryRelativePath(path, bucket);

            if (_entries.TryRemove(entryPath, out var entry))
            {
                lock (_evictionGate)
                {
                    _totalBytes -= entry.Size;
                }
            }

            try
            {
                var target = Path.Combine(_directory, entryPath);

                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删除失败留给 LRU 驱逐兜底。
            }
        }
    }

    /// <summary>校验条目头指纹与源文件当前 mtime + 大小是否一致。</summary>
    /// <remarks>指纹头存在条目字节开头；源文件只做属性 stat，不再打开文件读内容。</remarks>
    private static bool IsSourceUnchanged(string path, byte[] entryBytes)
    {
        try
        {
            var source = new FileInfo(path);

            if (!source.Exists)
            {
                return false;
            }

            return BitConverter.ToInt64(entryBytes, 0) == source.LastWriteTimeUtc.Ticks
                && BitConverter.ToInt64(entryBytes, 8) == source.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把源文件指纹写入条目流开头。</summary>
    private static void WriteHeader(Stream stream, FileInfo source)
    {
        Span<byte> header = stackalloc byte[HeaderLength];

        BitConverter.GetBytes(source.LastWriteTimeUtc.Ticks).AsSpan().CopyTo(header);
        BitConverter.GetBytes(source.Length).AsSpan().CopyTo(header[8..]);

        stream.Write(header);
    }

    /// <summary>路径哈希统一使用 UTF-8：跨进程 / 跨机器结果一致。</summary>
    private static readonly System.Text.Encoding TextEncoding = System.Text.Encoding.UTF8;

    /// <summary>计算条目相对路径：哈希前 4 个十六进制字符分为两级子目录，桶内文件名含档位前缀。</summary>
    private static string GetEntryRelativePath(string path, int bucket)
    {
        var hash = Convert.ToHexString(SHA256.HashData(TextEncoding.GetBytes(path)), 0, HashBytes);

        return Path.Combine(hash[..2], hash[2..4], $"{bucket}-{hash}.px");
    }

    /// <summary>记录一次命中：内存表立即刷新，磁盘 LastWriteTime 节流回写以持久化访问序。</summary>
    private void RecordHit(string entryPath)
    {
        var previous = _entries.GetOrAdd(entryPath, new Entry(0, DateTimeOffset.MinValue));
        _entries[entryPath] = previous.Touch();

        var lastAccess = previous.LastAccess;

        if (DateTimeOffset.UtcNow - lastAccess < TouchThrottle)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                File.SetLastWriteTimeUtc(Path.Combine(_directory, entryPath), DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // touch 失败仅影响重启后的 LRU 精度。
            }
        });
    }

    /// <summary>写入或更新条目元数据，并按需驱逐。</summary>
    private void Upsert(string entryPath, long entrySize)
    {
        lock (_evictionGate)
        {
            if (_entries.TryGetValue(entryPath, out var existing))
            {
                _totalBytes -= existing.Size;
            }

            _entries[entryPath] = new Entry(entrySize, DateTimeOffset.UtcNow);
            _totalBytes += entrySize;

            if (_totalBytes <= _capacityBytes)
            {
                return;
            }
        }

        Evict();
    }

    /// <summary>按访问序从旧到新驱逐，直到总量回到容量的 90%。</summary>
    private void Evict()
    {
        var target = (long)(_capacityBytes * EvictionTargetRatio);

        lock (_evictionGate)
        {
            foreach (var (entryPath, _) in _entries
                         .OrderBy(pair => pair.Value.LastAccess)
                         .ToList())
            {
                if (_totalBytes <= target)
                {
                    break;
                }

                if (!_entries.TryRemove(entryPath, out var entry))
                {
                    continue;
                }

                _totalBytes -= entry.Size;

                try
                {
                    File.Delete(Path.Combine(_directory, entryPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 条目正被读取时删除会失败：表已移除，磁盘空间由后续驱逐再回收。
                }
            }
        }
    }

    /// <summary>删除失效条目并回收容量计数。</summary>
    private void Discard(string entryPath)
    {
        if (_entries.TryRemove(entryPath, out var entry))
        {
            lock (_evictionGate)
            {
                _totalBytes -= entry.Size;
            }
        }

        try
        {
            var target = Path.Combine(_directory, entryPath);

            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败留给后续驱逐兜底。
        }
    }
}
