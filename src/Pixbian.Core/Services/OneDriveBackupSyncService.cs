/**
 * OneDrive 备份同步服务实现。
 * 职责：解析目标目录（用户指定优先，否则 OneDrive 的「应用」目录下的 Pixbian），
 *      成对写出用户数据包与索引库快照，并按份数成组清理旧备份。
 * 复用约定：用户数据走 IUserDataBackupService、索引库走 IDatabaseSnapshotService，
 *          内容与手动导出完全一致；时间取自 TimeProvider（便于测试注入）；
 *          OneDrive 探测顺序为环境变量 OneDrive → OneDriveConsumer → OneDriveCommercial
 *          → 用户目录下的 OneDrive 文件夹。
 * 关键约束：应用非常驻，「每天 / 每周 / 每月」由启动时检查 + 补齐错过周期实现；
 *          用户指定的目录必须是绝对路径（相对路径会落到进程当前目录，属误写）；
 *          两份文件共用同一时间戳，清理按时间戳成组删除，避免残留无法配对的孤儿文件；
 *          单个旧文件删除失败不影响本次同步结果。
 */

using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>OneDrive 备份同步服务。</summary>
public sealed class OneDriveBackupSyncService : IOneDriveBackupSyncService
{
    /// <summary>保留的备份组数（一组 = 同一时间戳的用户数据包 + 索引库快照）。</summary>
    private const int MaxBackupSets = 5;

    /// <summary>OneDrive 下的应用子目录名。</summary>
    private const string FolderName = "Pixbian";

    /// <summary>OneDrive 的「应用」目录在磁盘上的英文名：资源管理器按系统语言显示为「应用」。</summary>
    private const string AppsFolderName = "Apps";

    /// <summary>「应用」目录的本地化名兜底：用户或旧版本可能建的是中文名。</summary>
    private const string LocalizedAppsFolderName = "应用";

    private readonly IUserDataBackupService _backup;
    private readonly IDatabaseSnapshotService _snapshots;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化同步服务。</summary>
    /// <param name="backup">用户数据备份服务。</param>
    /// <param name="snapshots">索引库快照服务（同步时一并备份整库）。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public OneDriveBackupSyncService(
        IUserDataBackupService backup,
        IDatabaseSnapshotService snapshots,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(snapshots);

        _backup = backup;
        _snapshots = snapshots;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string? ResolveOneDriveFolder()
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var value = Environment.GetEnvironmentVariable(variable);

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                return value;
            }
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive");

        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <inheritdoc />
    public string? ResolveTargetFolder(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var configured = settings.BackupSyncFolder;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            // 只接受绝对路径：相对路径会被解析到进程当前目录（通常是安装目录），
            // 用户以为自己指定了位置、实际写到了别处，属静默误写。
            return Path.IsPathFullyQualified(configured) ? configured : null;
        }

        return ResolveOneDriveFolder() is { } root
            ? Path.Combine(ResolveAppsFolder(root), FolderName)
            : null;
    }

    /// <inheritdoc />
    public bool IsDue(AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.BackupSyncEnabled)
        {
            return false;
        }

        if (GetInterval(settings.BackupSyncFrequency) is not { } interval)
        {
            // 手动频率不参与自动判定，只能由「立即同步」触发。
            return false;
        }

        // 启用后从未成功同步过：立即补做一次。
        return settings.BackupSyncLastUtc is not { } last || now - last >= interval;
    }

    /// <inheritdoc />
    public async Task<BackupSyncResult> SyncAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var now = _timeProvider.GetUtcNow();
        var target = ResolveTargetFolder(settings);

        if (target is null)
        {
            return BackupSyncResult.Failure(
                "未找到同步目录：请安装并登录 OneDrive，或在设置中手动指定目录。",
                now);
        }

        try
        {
            Directory.CreateDirectory(target);

            // 两份文件共用同一时间戳：目录里同一时刻的备份是一组，清理时整组删除。
            var stamp = BackupFileNaming.FormatStamp(now.ToLocalTime());
            var userDataPath = Path.Combine(target, BackupFileNaming.BuildUserDataFileName(stamp));
            var indexPath = Path.Combine(target, BackupFileNaming.BuildIndexFileName(stamp));

            // 导出内容与「导出用户数据」完全一致（同一服务、同一格式）。
            await _backup.ExportAsync(userDataPath, settings.MusicLibraryPaths, cancellationToken)
                .ConfigureAwait(false);

            // 整库快照走 VACUUM INTO：直接复制库文件可能复制到只写了一半的 WAL 半成品。
            await _snapshots.CreateSnapshotAsync(indexPath, cancellationToken).ConfigureAwait(false);

            TrimOldBackups(target);

            return new BackupSyncResult
            {
                Succeeded = true,
                TargetFolder = target,
                FileNames = [Path.GetFileName(userDataPath), Path.GetFileName(indexPath)],
                CompletedUtc = now
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or NotSupportedException
                                      or ArgumentException or InvalidDataException)
        {
            return BackupSyncResult.Failure($"写入同步目录失败：{ex.Message}", now);
        }
    }

    /// <summary>只保留最近若干组备份；单个文件删除失败不影响同步结果。</summary>
    private static void TrimOldBackups(string target)
    {
        var sets = new DirectoryInfo(target)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .Select(file => (File: file, Stamp: BackupFileNaming.TryGetStamp(file.Name)))
            .Where(entry => entry.Stamp is not null)
            .GroupBy(entry => entry.Stamp!, StringComparer.Ordinal)

            // 时间戳是定长纯数字，字典序即时间序，无需解析成日期再比较。
            .OrderByDescending(group => group.Key, StringComparer.Ordinal)
            .ToList();

        // 整组删除：只删其中一份会留下永远无法配对的孤儿文件。
        foreach (var file in sets.Skip(MaxBackupSets).SelectMany(group => group))
        {
            try
            {
                file.File.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 被云客户端占用（正在上传）时会删不掉，留到下次同步再清即可。
            }
        }
    }

    /// <summary>「应用」目录：优先英文名，其次本地化名；两者都不存在时按英文名创建。</summary>
    private static string ResolveAppsFolder(string oneDriveRoot)
    {
        foreach (var name in new[] { AppsFolderName, LocalizedAppsFolderName })
        {
            var candidate = Path.Combine(oneDriveRoot, name);

            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(oneDriveRoot, AppsFolderName);
    }

    /// <summary>周期对应的时长；手动频率返回 null。</summary>
    private static TimeSpan? GetInterval(BackupSyncFrequency frequency) => frequency switch
    {
        BackupSyncFrequency.Daily => TimeSpan.FromDays(1),
        BackupSyncFrequency.Weekly => TimeSpan.FromDays(7),

        // 按月不引入日历运算：30 天是用户可预期的近似值，且不随月份长短抖动。
        BackupSyncFrequency.Monthly => TimeSpan.FromDays(30),
        _ => null
    };
}
