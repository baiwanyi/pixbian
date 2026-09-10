/**
 * OneDrive 备份同步服务的单元测试。
 * 职责：锁定周期判定（未启用 / 手动 / 从未同步 / 未到期 / 已到期）与同步落盘行为
 *      （写入固定名 latest、生成历史副本、只保留最近若干份、目录不可用时失败而不误写）。
 * 复用约定：导出经假的 IUserDataBackupService（只落一个占位文件），
 *          时间经假的 TimeProvider 固定，目标目录一律指向临时目录。
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
    public async Task SyncAsync_写入latest与历史副本_并只保留最近份数()
    {
        var target = CreateTemporaryDirectory();
        var settings = new AppSettings { BackupSyncEnabled = true, BackupSyncFolder = target };
        var service = CreateService();

        // 预置 7 份历史副本，模拟已同步多次的目录状态。
        var historyFolder = Path.Combine(target, "history");
        Directory.CreateDirectory(historyFolder);

        for (var index = 0; index < 7; index++)
        {
            var file = Path.Combine(historyFolder, $"Pixbian-userdata-PC-2026090{index + 1}-000000.json");
            await File.WriteAllTextAsync(file, "{}");
            File.SetLastWriteTimeUtc(file, FixedTime.UtcDateTime.AddDays(index));
        }

        var result = await service.SyncAsync(settings);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(target, "Pixbian-userdata-latest.json")));
        Assert.Equal(5, Directory.GetFiles(historyFolder, "*.json").Length);
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
        new(new FakeBackupService(), new FixedTimeProvider(FixedTime));

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
            var directory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(destinationPath, "{}");
            return Task.FromResult(new UserDataBackupCounts());
        }

        public Task<UserDataImportResult> ImportAsync(
            string sourcePath,
            UserDataImportOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
