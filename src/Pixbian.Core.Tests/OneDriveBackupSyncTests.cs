/**
 * OneDrive 备份同步服务的单元测试。
 * 职责：锁定周期判定（未启用 / 手动 / 从未同步 / 未到期 / 已到期）与同步落盘行为
 *      （成对写出用户数据包与索引库快照、按时间戳成组清理、目录不可用时失败而不误写）。
 * 复用约定：导出经假的 IUserDataBackupService、快照经假的 IDatabaseSnapshotService
 *          （只落占位文件），时间经假的 TimeProvider 固定，目标目录一律指向临时目录。
 * 关键约束：测试**不得**在未显式指定目录的情况下调用 SyncAsync——那会走 OneDrive 探测
 *          并把文件写进开发者真实的 OneDrive 目录。
 */

using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>OneDrive 备份同步服务测试。</summary>
public sealed class OneDriveBackupSyncTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _temporaryDirectories = [];

    /// <summary>清理测试创建的临时目录。</summary>
    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响断言结果。
            }
        }
    }

    [Fact]
    public void IsDue_未启用同步_恒为false()
    {
        var service = CreateService();
        var settings = new AppSettings { BackupSyncEnabled = false, BackupSyncFrequency = BackupSyncFrequency.Daily };

        Assert.False(service.IsDue(settings, FixedTime));
    }

    [Fact]
    public void IsDue_手动频率_恒为false()
    {
        var service = CreateService();
        var settings = new AppSettings
        {
            BackupSyncEnabled = true,
            BackupSyncFrequency = BackupSyncFrequency.Manual
        };

        // 手动频率只能由「立即同步」触发，不参与自动判定——即便从未同步过也不算到期。
        Assert.False(service.IsDue(settings, FixedTime));
    }

    [Fact]
    public void IsDue_启用且从未同步过_视为到期()
    {
        var service = CreateService();
        var settings = new AppSettings
        {
            BackupSyncEnabled = true,
            BackupSyncFrequency = BackupSyncFrequency.Daily,
            BackupSyncLastUtc = null
        };

        Assert.True(service.IsDue(settings, FixedTime));
    }

    [Theory]
    [InlineData(BackupSyncFrequency.Daily, 12, false)]
    [InlineData(BackupSyncFrequency.Daily, 25, true)]
    [InlineData(BackupSyncFrequency.Weekly, 24 * 3, false)]
    [InlineData(BackupSyncFrequency.Weekly, 24 * 8, true)]
    [InlineData(BackupSyncFrequency.Monthly, 24 * 20, false)]
    [InlineData(BackupSyncFrequency.Monthly, 24 * 31, true)]
    public void IsDue_按周期判定是否到期(BackupSyncFrequency frequency, int elapsedHours, bool expected)
    {
        var service = CreateService();
        var settings = new AppSettings
        {
            BackupSyncEnabled = true,
            BackupSyncFrequency = frequency,
            BackupSyncLastUtc = FixedTime.AddHours(-elapsedHours)
        };

        Assert.Equal(expected, service.IsDue(settings, FixedTime));
    }

    [Fact]
    public async Task SyncAsync_成对写入用户数据包与索引库快照()
    {
        var target = CreateTemporaryDirectory();
        var settings = new AppSettings { BackupSyncEnabled = true, BackupSyncFolder = target };

        var result = await CreateService().SyncAsync(settings);

        Assert.True(result.Succeeded);
        Assert.Equal(target, result.TargetFolder);

        // 时间戳取本地时间：断言与实现同源计算，测试不随运行机器的时区变化。
        var stamp = BackupFileNaming.FormatStamp(FixedTime.ToLocalTime());
        var userDataName = BackupFileNaming.BuildUserDataFileName(stamp);
        var indexName = BackupFileNaming.BuildIndexFileName(stamp);

        Assert.Equal([userDataName, indexName], result.FileNames);
        Assert.True(File.Exists(Path.Combine(target, userDataName)));
        Assert.True(File.Exists(Path.Combine(target, indexName)));
    }

    [Fact]
    public async Task SyncAsync_按时间戳成组清理_保留最近五组且不残留半个()
    {
        var target = CreateTemporaryDirectory();
        var settings = new AppSettings { BackupSyncEnabled = true, BackupSyncFolder = target };

        // 预置 7 组旧备份（每组 json + db），时间戳递增。
        for (var index = 1; index <= 7; index++)
        {
            var stamp = $"2026090{index}-000000";
            await File.WriteAllTextAsync(Path.Combine(target, BackupFileNaming.BuildUserDataFileName(stamp)), "{}");
            await File.WriteAllTextAsync(Path.Combine(target, BackupFileNaming.BuildIndexFileName(stamp)), "db");
        }

        // 目录里与备份无关的文件不得被误删。
        var unrelated = Path.Combine(target, "notes.txt");
        await File.WriteAllTextAsync(unrelated, "keep me");

        var result = await CreateService().SyncAsync(settings);

        Assert.True(result.Succeeded);

        var sets = Directory.GetFiles(target)
            .Select(Path.GetFileName)
            .Select(name => BackupFileNaming.TryGetStamp(name!))
            .Where(stamp => stamp is not null)
            .GroupBy(stamp => stamp!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count());

        // 7 组旧 + 1 组新 = 8 组，只留最近 5 组，且每组必须仍是 json + db 成对。
        Assert.Equal(5, sets.Count);
        Assert.All(sets.Values, count => Assert.Equal(2, count));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task SyncAsync_目录为相对路径_返回失败而非写到当前目录()
    {
        var settings = new AppSettings
        {
            BackupSyncEnabled = true,
            BackupSyncFolder = "relative\\nested"
        };

        var result = await CreateService().SyncAsync(settings);

        // 相对路径会被解析到进程当前目录（通常是安装目录），属静默误写：直接判失败。
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("同步目录", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "relative")));
    }

    /// <summary>创建目标目录为临时目录的同步服务（避免触碰真实的 OneDrive 目录）。</summary>
    private static OneDriveBackupSyncService CreateService() =>
        new(new FakeBackupService(), new FakeSnapshotService(), new FixedTimeProvider(FixedTime));

    /// <summary>写出占位文件（两个假服务共用）：同步逻辑只关心文件是否落到目标目录。</summary>
    private static void WritePlaceholder(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, "placeholder");
    }

    private string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }

    /// <summary>固定时间的 TimeProvider。</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>只落一个占位文件的备份服务：同步逻辑不关心导出内容。</summary>
    private sealed class FakeBackupService : IUserDataBackupService
    {
        public Task<UserDataBackupCounts> ExportAsync(
            string destinationPath,
            IReadOnlyList<string> musicFolders,
            CancellationToken cancellationToken = default)
        {
            WritePlaceholder(destinationPath);
            return Task.FromResult(new UserDataBackupCounts());
        }

        public Task<UserDataImportResult> ImportAsync(
            string sourcePath,
            UserDataImportOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>只落一个占位文件的快照服务：同步逻辑不关心库内容。</summary>
    private sealed class FakeSnapshotService : IDatabaseSnapshotService
    {
        public Task<string> CreateSnapshotAsync(
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            WritePlaceholder(destinationPath);
            return Task.FromResult(destinationPath);
        }

        public Task<string> RestoreSnapshotAsync(
            string snapshotPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int CleanupObsoleteBackups(int keepCount, TimeSpan maxAge) => 0;
    }
}
