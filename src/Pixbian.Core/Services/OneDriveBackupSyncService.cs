/**
 * OneDrive 备份同步服务实现。
 * 职责：解析目标目录（用户指定优先，否则探测 OneDrive 根），导出用户数据备份，
 *      写入固定名 latest 文件与一份历史副本，并按保留份数清理旧副本。
 * 复用约定：导出复用 IUserDataBackupService；时间取自 TimeProvider（便于测试注入）；
 *          OneDrive 探测顺序为环境变量 OneDrive → OneDriveConsumer → OneDriveCommercial
 *          → 用户目录下的 OneDrive 文件夹。
 * 关键约束：应用非常驻，「每天 / 每周 / 每月」由启动时检查 + 补齐错过周期实现；
 *          用户指定的目录必须是绝对路径（相对路径会落到进程当前目录，属误写）；
 *          历史副本按机器名 + 时间戳命名，避免多机互相覆盖；保留份数固定为 5；
 *          单个历史文件删除失败不影响本次同步结果。
 */

using System.Globalization;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>OneDrive 备份同步服务。</summary>
public sealed class OneDriveBackupSyncService : IOneDriveBackupSyncService
{
    /// <summary>保留的历史副本份数（不含 latest）。</summary>
    private const int MaxHistoryFiles = 5;

    /// <summary>OneDrive 下的应用子目录名。</summary>
    private const string FolderName = "Pixbian";

    /// <summary>固定名主备份文件：其它电脑导入时只需认这一个文件名。</summary>
    private const string LatestFileName = "Pixbian-userdata-latest.json";

    /// <summary>历史副本子目录名。</summary>
    private const string HistoryFolderName = "history";

    private readonly IUserDataBackupService _backup;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化同步服务。</summary>
    /// <param name="backup">备份服务。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public OneDriveBackupSyncService(IUserDataBackupService backup, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backup);

        _backup = backup;
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

        return ResolveOneDriveFolder() is { } root ? Path.Combine(root, FolderName) : null;
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
            var latestPath = Path.Combine(target, LatestFileName);

            // 导出内容与「导出用户数据」完全一致（同一服务、同一格式）。
            await _backup.ExportAsync(latestPath, settings.MusicLibraryPaths, cancellationToken)
                .ConfigureAwait(false);

            WriteHistoryCopy(target, latestPath);

            return new BackupSyncResult
            {
                Succeeded = true,
                TargetPath = latestPath,
                CompletedUtc = now
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or NotSupportedException
                                      or ArgumentException)
        {
            return BackupSyncResult.Failure($"写入同步目录失败：{ex.Message}", now);
        }
    }

    /// <summary>写一份带机器名与时间戳的历史副本，并按保留份数清理旧文件。</summary>
    private void WriteHistoryCopy(string target, string latestPath)
    {
        var historyFolder = Path.Combine(target, HistoryFolderName);
        Directory.CreateDirectory(historyFolder);

        var stamp = _timeProvider.GetUtcNow()
            .ToLocalTime()
            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"Pixbian-userdata-{SanitizeMachineName(Environment.MachineName)}-{stamp}.json";

        File.Copy(latestPath, Path.Combine(historyFolder, fileName), overwrite: true);

        TrimHistory(historyFolder);
    }

    /// <summary>只保留最近若干份历史副本；单个文件删除失败不影响同步结果。</summary>
    private static void TrimHistory(string historyFolder)
    {
        var files = new DirectoryInfo(historyFolder)
            .GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        foreach (var file in files.Skip(MaxHistoryFiles))
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 被云客户端占用（正在上传）时会删不掉，留到下次同步再清即可。
            }
        }
    }

    /// <summary>机器名可能含文件名非法字符（域环境下的主机名更常见），裁剪后再用于文件名。</summary>
    private static string SanitizeMachineName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(character => !invalid.Contains(character) && character != ' ').ToArray());

        return cleaned.Length == 0 ? "PC" : cleaned;
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
