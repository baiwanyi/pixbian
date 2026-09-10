/**
 * 媒体库索引服务：把本地文件夹中的图片与视频扫描入库，并与既有索引做对账。
 * 职责：枚举受支持文件、批量写入索引、移除已失效记录、更新扫描源的最后扫描时间。
 * 复用约定：目录枚举经 IMediaFileEnumerator 注入（默认实现走 System.IO.EnumerationOptions），
 *          便于测试替换；时间统一取自注入的 TimeProvider，便于测试。
 * 关键约束：枚举必须跳过重解析点（符号链接与目录联接），否则会遇到目录环或读取到库外内容；
 *          对账须在本次扫描全部写入完成后进行，中途失败不得触发删除，以防数据丢失；
 *          【对账误删防护】枚举期间出现不可访问目录时，本次扫描一律不执行对账删除——
 *          目录不可访问会让其子树的条目从「本次发现集合」中缺席，此时对账会把这些仍然存在
 *          的条目误判为失效并连带删除收藏、分类与分组关系，属不可逆损失；
 *          宁可留下僵尸条目（待下次成功扫描清理），也绝不误删；
 *          IsFavorite、Rating、CategoryId 等用户数据的覆盖由仓储层拦截，本服务不感知。
 */

using System.IO;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;

namespace Pixbian.Core.Services;

/// <summary>媒体库索引服务。</summary>
public sealed class MediaIndexingService
{
    private const int BatchSize = 500;

    /// <summary>对账删除的批大小：失效条目按批提交，内存占用与子树规模解耦。</summary>
    private const int ReconcileDeleteBatchSize = 500;

    private readonly IMediaItemRepository _mediaItems;
    private readonly ILibraryFolderRepository _libraryFolders;
    private readonly TimeProvider _timeProvider;
    private readonly IMediaFileEnumerator _fileEnumerator;

    /// <summary>初始化索引服务。</summary>
    /// <param name="mediaItems">媒体条目仓储。</param>
    /// <param name="libraryFolders">扫描源仓储。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    /// <param name="fileEnumerator">文件枚举器；为空时使用真实文件系统实现，仅供测试注入。</param>
    public MediaIndexingService(
        IMediaItemRepository mediaItems,
        ILibraryFolderRepository libraryFolders,
        TimeProvider? timeProvider = null,
        IMediaFileEnumerator? fileEnumerator = null)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(libraryFolders);

        _mediaItems = mediaItems;
        _libraryFolders = libraryFolders;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fileEnumerator = fileEnumerator ?? FileSystemMediaFileEnumerator.Instance;
    }

    /// <summary>扫描指定扫描源并同步索引。</summary>
    /// <param name="folder">扫描源。</param>
    /// <param name="progress">进度上报器；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>索引结果报告。</returns>
    public async Task<IndexingReport> ScanAsync(
        LibraryFolder folder,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder.Path);

        var root = PathGuard.NormalizeDirectory(folder.Path);
        var indexedUtc = _timeProvider.GetUtcNow();
        // 直接累积为集合：对账只需要「本次发现了哪些路径」的成员判定，
        // 百万条规模下再保留一份列表会平白多出上百 MB 的引用开销。
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<MediaItem>(BatchSize);
        var inaccessibleDirectories = new InaccessibleDirectoryCounter();
        var indexed = 0;

        foreach (var file in _fileEnumerator.Enumerate(root, inaccessibleDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = CreateItem(file, indexedUtc);
            batch.Add(item);
            discovered.Add(item.Path);

            if (batch.Count < BatchSize)
            {
                continue;
            }

            await _mediaItems.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            indexed += batch.Count;
            batch.Clear();
            progress?.Report(new IndexingProgress(indexed));
        }

        if (batch.Count > 0)
        {
            await _mediaItems.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            indexed += batch.Count;
            batch.Clear();
            progress?.Report(new IndexingProgress(indexed));
        }

        // 存在不可访问目录时放弃对账：其子树的条目本次不会被发现，
        // 照常对账会把它们误判为失效并连带删除收藏、分类与分组关系（不可逆）。
        var skippedReconcile = inaccessibleDirectories.Count > 0;

        var removed = skippedReconcile
            ? 0
            : await ReconcileAsync(root, discovered, cancellationToken).ConfigureAwait(false);

        await _libraryFolders
            .UpdateLastScanAsync(folder.Id, indexedUtc, cancellationToken)
            .ConfigureAwait(false);

        return new IndexingReport(
            indexed,
            removed,
            indexedUtc,
            inaccessibleDirectories.Count,
            skippedReconcile);
    }

    /// <summary>由文件信息构造媒体条目。</summary>
    private static MediaItem CreateItem(FileInfo file, DateTimeOffset indexedUtc)
    {
        var createdUtc = new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
        var modifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

        return new MediaItem
        {
            Path = file.FullName,
            FileName = file.Name,
            Directory = file.DirectoryName ?? string.Empty,
            Kind = MediaFileClassifier.Classify(file.Name),
            FileSize = file.Length,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc,
            IndexedUtc = indexedUtc,

            // 元数据解析在 M3/M4 完成，此处以文件系统时间兜底，取两者中较早的一个。
            TakenUtc = createdUtc <= modifiedUtc ? createdUtc : modifiedUtc
        };
    }

    /// <summary>移除目录中已不存在于文件系统的失效条目。</summary>
    /// <remarks>
    /// 逐条流式读取既有路径并即时比对，不再把整棵子树的路径物化成列表：
    /// 百万条规模下一次对账会让数百 MB 路径同时驻留托管堆（实测短路径单份约 69 MB，
    /// 按真实路径长度折算约 130 MB），造成明显的 GC 压力。
    /// 失效条目按批删除，内存占用与子树规模解耦。
    /// </remarks>
    private async Task<int> ReconcileAsync(
        string root,
        HashSet<string> found,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        var stale = new List<string>(ReconcileDeleteBatchSize);

        await foreach (var path in _mediaItems
                           .EnumeratePathsUnderDirectoryAsync(root, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (found.Contains(path))
            {
                continue;
            }

            stale.Add(path);

            if (stale.Count < ReconcileDeleteBatchSize)
            {
                continue;
            }

            await _mediaItems.DeleteByPathsAsync(stale, cancellationToken).ConfigureAwait(false);
            removed += stale.Count;
            stale.Clear();
        }

        if (stale.Count > 0)
        {
            await _mediaItems.DeleteByPathsAsync(stale, cancellationToken).ConfigureAwait(false);
            removed += stale.Count;
        }

        return removed;
    }
}
