/**
 * 音乐库服务模块。
 * 职责：扫描用户配置的音乐目录，把受支持的音频文件全量写入 music_tracks 表，
 *      并向短片页提供随机抽曲能力（视频无音轨时作为背景音乐）。
 * 复用约定：目录来自用户配置，枚举结果一律经 PathGuard 校验归属后再收录，杜绝路径穿越；
 *          落库口径与 IMusicTrackRepository 一致，采用「全量替换」而非增量对账。
 * 关键约束：本服务只为短片页提供背景音乐候选，绝不参与图库索引、缩略图与元数据回填；
 *          递归枚举必须走 Task.Run 让出 UI 线程——机械盘上扫描上千文件耗时可达秒级；
 *          互锁用 Interlocked 而非 SemaphoreSlim 字段：
 *          持有可释放字段会触发 CA1001，在本项目的 -warnaserror 下是构建错误。
 */

using System.IO;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;

namespace Pixbian.Core.Services;

/// <summary>音乐库服务。</summary>
public interface IMusicLibraryService
{
    /// <summary>当前内存中的曲目路径快照；尚未载入或库为空时为空集合。</summary>
    IReadOnlyList<string> TrackPaths { get; }

    /// <summary>把库内既有曲目载入内存；不触碰磁盘，供启动阶段快速恢复候选池。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>扫描全部音乐目录并全量替换库中曲目。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扫描得到的曲目数量；已在扫描中时返回当前快照数量。</returns>
    Task<int> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>随机取出一首曲目；库为空时返回 null。</summary>
    /// <returns>曲目完整路径；无可用曲目时为 null。</returns>
    string? TakeRandomTrack();
}

/// <summary>基于文件系统的音乐库服务。</summary>
public sealed class MusicLibraryService : IMusicLibraryService
{
    /// <summary>避重重试次数；多于一次即可，再多只是极端情况下的兜底。</summary>
    private const int PickAttempts = 3;

    private readonly IMusicTrackRepository _tracks;
    private readonly ISettingsService _settings;
    private readonly object _pickLock = new();

    private IReadOnlyList<string> _trackPaths = [];
    private string? _lastPicked;

    /// <summary>扫描是否已在运行（互锁标志，1 表示在跑）。</summary>
    private int _scanRunning;

    /// <summary>初始化音乐库服务。</summary>
    /// <param name="tracks">音乐曲目仓储。</param>
    /// <param name="settings">设置服务，提供音乐库目录配置。</param>
    public MusicLibraryService(IMusicTrackRepository tracks, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(settings);

        _tracks = tracks;
        _settings = settings;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> TrackPaths => _trackPaths;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var tracks = await _tracks.GetAllAsync(cancellationToken).ConfigureAwait(false);
        _trackPaths = tracks.Select(t => t.Path).ToList();
    }

    /// <inheritdoc />
    public async Task<int> ScanAsync(CancellationToken cancellationToken = default)
    {
        // 已在扫描中：直接返回当前快照数量，不排队、不并发写库。
        // 两个扫描任务会各自全量替换同一张表，后完成的覆盖先完成的，属于无谓的磁盘与数据库争用。
        if (Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0)
        {
            return _trackPaths.Count;
        }

        try
        {
            var roots = _settings.Current.MusicLibraryPaths;
            var scanned = await Task.Run(() => EnumerateTracks(roots), cancellationToken)
                .ConfigureAwait(false);

            await _tracks.ReplaceAllAsync(scanned, cancellationToken).ConfigureAwait(false);

            _trackPaths = scanned.Select(t => t.Path).ToList();

            lock (_pickLock)
            {
                // 曲目集合已整体替换，上一首的记忆可能已不在库中，重置以免误判重复。
                _lastPicked = null;
            }

            return scanned.Count;
        }
        finally
        {
            Interlocked.Exchange(ref _scanRunning, 0);
        }
    }

    /// <inheritdoc />
    public string? TakeRandomTrack()
    {
        // 先取到本地变量：扫描完成会整体替换该引用，锁定期间直接读字段会读到被换掉的集合。
        var paths = _trackPaths;

        if (paths.Count == 0)
        {
            return null;
        }

        lock (_pickLock)
        {
            if (paths.Count == 1)
            {
                _lastPicked = paths[0];
                return paths[0];
            }

            for (var attempt = 0; attempt < PickAttempts; attempt++)
            {
                var candidate = paths[Random.Shared.Next(paths.Count)];

                if (!string.Equals(candidate, _lastPicked, StringComparison.OrdinalIgnoreCase))
                {
                    _lastPicked = candidate;
                    return candidate;
                }
            }

            _lastPicked = paths[Random.Shared.Next(paths.Count)];
            return _lastPicked;
        }
    }

    /// <summary>递归枚举各音乐目录下的受支持音频文件。</summary>
    /// <param name="roots">音乐库根目录集合。</param>
    /// <returns>曲目集合；目录不存在、不可访问或路径非法时跳过该目录。</returns>
    private static List<MusicTrack> EnumerateTracks(IReadOnlyList<string> roots)
    {
        var result = new List<MusicTrack>();

        if (roots.Count == 0)
        {
            return result;
        }

        var now = TimeProvider.System.GetUtcNow();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            string normalizedRoot;

            try
            {
                normalizedRoot = PathGuard.NormalizeDirectory(root);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,

                // 权限不足的子目录直接跳过：一个不可读目录不应让整次扫描失败。
                IgnoreInaccessible = true,

                // 跳过重解析点（符号链接 / 目录联接）：既避免枚举陷入环，
                // 也杜绝经链接跳出用户配置的音乐目录。
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(normalizedRoot, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!AudioFileClassifier.IsAudio(file))
                {
                    continue;
                }

                // 枚举结果理论上已在根目录之内，仍显式校验一次：
                // AttributesToSkip 不拦硬链接，归属校验是「只收录用户配置目录内文件」的最后一道闸。
                if (!PathGuard.IsInside(normalizedRoot, file))
                {
                    continue;
                }

                long fileSize;

                try
                {
                    fileSize = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                result.Add(new MusicTrack
                {
                    Path = file,
                    FileName = Path.GetFileName(file),
                    Directory = Path.GetDirectoryName(file) ?? string.Empty,
                    FileSize = fileSize,
                    AddedUtc = now
                });
            }
        }

        return result;
    }
}
