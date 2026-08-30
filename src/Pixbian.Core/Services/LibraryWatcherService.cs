/**
 * 媒体库目录监控服务：监视扫描源的文件变更并聚合成批次后上报。
 * 职责：为每个启用的扫描源建立 FileSystemWatcher，把新建、修改、重命名、删除事件去重聚合后抛出。
 * 复用约定：事件经 Channel 解耦生产者与消费者，上报前统一用 PathGuard 校验路径归属；
 *          日志通过 Microsoft.Extensions.Logging 抽象输出，不直接依赖具体日志实现。
 * 关键约束：FileSystemWatcher 在批量操作时会溢出内部缓冲区并丢失事件（Error 事件），
 *          此时必须记录告警并由调用方触发全量回扫兜底，不能假定事件流是完整的；
 *          静默期聚合窗口用于抑制高频事件风暴（如批量重命名），避免逐条回扫拖垮索引。
 */

using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;

namespace Pixbian.Core.Services;

/// <summary>媒体库目录监控服务。</summary>
public sealed partial class LibraryWatcherService : IAsyncDisposable
{
    // 日志统一使用 LoggerMessage 源生成器，避免每次调用都解析消息模板并装箱参数（CA1848）。
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "目录监控已启动，共监视 {Count} 个扫描源。")]
    private static partial void LogStarted(ILogger logger, int count);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "扫描源路径无效，已跳过：{Path}")]
    private static partial void LogInvalidPath(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "扫描源目录不存在，已跳过：{Path}")]
    private static partial void LogDirectoryMissing(ILogger logger, string path);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "变更路径越出扫描源范围，已忽略：{Path}")]
    private static partial void LogPathOutsideRoot(ILogger logger, string path);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "变更队列已关闭，丢弃变更：{Path}")]
    private static partial void LogQueueClosed(ILogger logger, string path);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning,
        Message = "目录监控缓冲区溢出或发生错误，事件可能已丢失，建议对该扫描源执行全量回扫：{Path}")]
    private static partial void LogWatcherError(ILogger logger, Exception? exception, string path);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information,
        Message = "目录变更已聚合，本批次 {Count} 条。")]
    private static partial void LogBatchAggregated(ILogger logger, int count);

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LibraryWatcherService> _logger;
    private readonly Channel<LibraryChange> _queue =
        Channel.CreateUnbounded<LibraryChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly List<FileSystemWatcher> _watchers = [];

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private bool _disposed;

    /// <summary>初始化监控服务。</summary>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    /// <param name="logger">日志记录器；为空时使用空实现。</param>
    public LibraryWatcherService(
        TimeProvider? timeProvider = null,
        ILogger<LibraryWatcherService>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<LibraryWatcherService>.Instance;
    }

    /// <summary>一批变更已完成聚合时触发。</summary>
    public event EventHandler<LibraryChangeEventArgs>? ChangesDetected;

    /// <summary>为指定的扫描源启动监控。</summary>
    /// <param name="folders">扫描源列表；未启用的会被跳过。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">服务已启动时抛出。</exception>
    public Task StartAsync(
        IReadOnlyList<LibraryFolder> folders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pumpTask is not null)
        {
            throw new InvalidOperationException("监控服务已启动，请勿重复调用 StartAsync。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var folder in folders)
        {
            if (!folder.IsEnabled)
            {
                continue;
            }

            if (!TryCreateWatcher(folder, out var watcher))
            {
                continue;
            }

            _watchers.Add(watcher);
        }

        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);

        LogStarted(_logger, _watchers.Count);
        return Task.CompletedTask;
    }

    /// <summary>停止监控并释放全部资源。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _queue.Writer.TryComplete();

        if (_pumpCts is null)
        {
            return;
        }

        await _pumpCts.CancelAsync().ConfigureAwait(false);

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消导致的退出属于预期行为。
            }
        }

        _pumpCts.Dispose();
    }

    /// <summary>创建并启动单个扫描源的监视器。</summary>
    private bool TryCreateWatcher(LibraryFolder folder, out FileSystemWatcher watcher)
    {
        string root;
        try
        {
            root = PathGuard.NormalizeDirectory(folder.Path);
        }
        catch (ArgumentException ex)
        {
            LogInvalidPath(_logger, ex, folder.Path);
            watcher = null!;
            return false;
        }

        if (!Directory.Exists(root))
        {
            LogDirectoryMissing(_logger, root);
            watcher = null!;
            return false;
        }

        var instance = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime
        };

        instance.Created += (_, e) => Publish(LibraryChangeKind.Created, root, e.FullPath, e.Name, null);
        instance.Changed += (_, e) => Publish(LibraryChangeKind.Modified, root, e.FullPath, e.Name, null);
        instance.Deleted += (_, e) => Publish(LibraryChangeKind.Deleted, root, e.FullPath, e.Name, null);
        instance.Renamed += (_, e) => Publish(LibraryChangeKind.Renamed, root, e.FullPath, e.Name, e.OldFullPath);
        instance.Error += (_, e) => OnWatcherError(root, e.GetException());
        instance.EnableRaisingEvents = true;

        watcher = instance;
        return true;
    }

    /// <summary>校验路径归属并入队；越界或不受支持的变更直接丢弃。</summary>
    private void Publish(
        LibraryChangeKind kind,
        string root,
        string fullPath,
        string? name,
        string? oldFullPath)
    {
        if (name is not null && !MediaFileClassifier.IsSupported(name))
        {
            return;
        }

        if (!PathGuard.TryResolveInside(root, fullPath, out var resolvedPath))
        {
            LogPathOutsideRoot(_logger, fullPath);
            return;
        }

        string? resolvedOldPath = null;
        if (oldFullPath is not null)
        {
            PathGuard.TryResolveInside(root, oldFullPath, out resolvedOldPath);
        }

        if (!_queue.Writer.TryWrite(new LibraryChange(kind, resolvedPath, resolvedOldPath)))
        {
            LogQueueClosed(_logger, resolvedPath);
        }
    }

    /// <summary>监视器内部缓冲区溢出时的处理。</summary>
    private void OnWatcherError(string root, Exception? exception)
    {
        LogWatcherError(_logger, exception, root);
    }

    /// <summary>后台泵：持续收集事件，静默期结束后批量上报。</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var batch = new Dictionary<string, LibraryChange>(StringComparer.OrdinalIgnoreCase);
            var lastEventAt = _timeProvider.GetUtcNow();

            // 持续收集直至静默期结束；同一路径的多次变更只保留最后一次。
            while (_timeProvider.GetUtcNow() - lastEventAt < QuietPeriod)
            {
                if (_queue.Reader.TryRead(out var change))
                {
                    batch[change.Path] = change;
                    lastEventAt = _timeProvider.GetUtcNow();
                    continue;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            if (batch.Count > 0)
            {
                var changes = batch.Values.ToList();
                LogBatchAggregated(_logger, changes.Count);
                ChangesDetected?.Invoke(this, new LibraryChangeEventArgs(changes));
            }
        }
    }
}
