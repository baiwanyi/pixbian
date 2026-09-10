/**
 * 数据备份视图模型（设置页「数据与备份」分区）。
 * 职责：编排用户数据的导出与导入——导出索引库中的收藏、分组、分类与规则、扫描源与音乐目录；
 *      导入按「合并、导入文件为准」策略写回，并在成功后刷新各视图模型使界面与库一致。
 * 复用约定：导出/导入一律经 IUserDataBackupService，本类不直接触碰数据库；
 *          音乐库目录取自 ISettingsService，导入时与本地列表合并后落盘；
 *          刷新复用各视图模型既有的 LoadAsync / ReloadAsync，不自行重查数据。
 * 关键约束：导入会改写用户数据，必须由调用方完成二次确认后才执行；
 *          文件选择由页面负责（需要 WindowId），本类只接收路径；
 *          导入失败不吞异常原因——非法格式、版本过高、文件过大等必须如实呈现给用户。
 */

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.ViewModels;

/// <summary>数据备份视图模型。</summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly IUserDataBackupService _backup;
    private readonly IDatabaseSnapshotService _snapshots;
    private readonly IOneDriveBackupSyncService _sync;
    private readonly ISettingsService _settings;
    private readonly SettingsViewModel _settingsViewModel;

    /// <summary>旧库副本至少保留的份数（超出部分在启动时清理）。</summary>
    private const int KeepBackupCopies = 3;

    /// <summary>旧库副本的最大保留时长：即便还在保留份数内，超过该时长也会被清理。</summary>
    private static readonly TimeSpan MaxBackupAge = TimeSpan.FromDays(30);
    private readonly CategoryViewModel _categories;
    private readonly FavoriteGroupViewModel _favoriteGroups;
    private readonly GalleryViewModel _gallery;

    private bool _isBusy;

    /// <summary>初始化数据备份视图模型。</summary>
    /// <param name="backup">备份服务。</param>
    /// <param name="snapshots">索引库快照服务（整库备份与还原）。</param>
    /// <param name="sync">OneDrive 同步服务。</param>
    /// <param name="settings">设置服务（提供音乐库目录与同步配置）。</param>
    /// <param name="settingsViewModel">设置视图模型（导入后刷新扫描源与音乐目录）。</param>
    /// <param name="categories">分类视图模型（导入后刷新分类与规则）。</param>
    /// <param name="favoriteGroups">收藏分组视图模型（导入后刷新分组）。</param>
    /// <param name="gallery">图库视图模型（导入后重新加载当前视图）。</param>
    public BackupViewModel(
        IUserDataBackupService backup,
        IDatabaseSnapshotService snapshots,
        IOneDriveBackupSyncService sync,
        ISettingsService settings,
        SettingsViewModel settingsViewModel,
        CategoryViewModel categories,
        FavoriteGroupViewModel favoriteGroups,
        GalleryViewModel gallery)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsViewModel);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(favoriteGroups);
        ArgumentNullException.ThrowIfNull(gallery);

        _backup = backup;
        _snapshots = snapshots;
        _sync = sync;
        _settings = settings;
        _settingsViewModel = settingsViewModel;
        _categories = categories;
        _favoriteGroups = favoriteGroups;
        _gallery = gallery;
    }

    /// <summary>导出结果的说明文本。</summary>
    [ObservableProperty]
    private string _exportStatusText = "尚未导出；导出的文件可复制到其它电脑导入，或交由 OneDrive 同步。";

    /// <summary>导入结果的说明文本。</summary>
    [ObservableProperty]
    private string _importStatusText = "导入采用合并方式：收藏取并集，分类与分组按名称合并。";

    /// <summary>同步状态说明文本（目标目录、上次同步时间或失败原因）。</summary>
    [ObservableProperty]
    private string _syncStatusText = "尚未同步。";

    /// <summary>同步开关的当前值。</summary>
    public bool IsSyncEnabled => _settings.Current.BackupSyncEnabled;

    /// <summary>同步周期下拉的当前索引（与 <see cref="BackupSyncFrequency"/> 数值一致）。</summary>
    public int SyncFrequencyIndex => (int)_settings.Current.BackupSyncFrequency;

    /// <summary>快照（整库备份）的状态说明文本。</summary>
    [ObservableProperty]
    private string _snapshotStatusText = "快照含索引与全部用户数据，仅用于本机还原；跨机迁移请用「用户数据」。";

    /// <summary>导出当前索引库的整库快照。</summary>
    /// <param name="destinationPath">快照文件路径。</param>
    /// <returns>是否成功。</returns>
    public async Task<bool> CreateSnapshotAsync(string destinationPath)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;

        try
        {
            var path = await _snapshots.CreateSnapshotAsync(destinationPath).ConfigureAwait(true);
            SnapshotStatusText = $"已备份到 {path}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                      or ArgumentException or InvalidOperationException)
        {
            SnapshotStatusText = $"备份失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 用快照替换当前索引库；成功后返回旧库备份路径（调用方据此提示用户重启应用）。
    /// </summary>
    /// <param name="snapshotPath">快照文件路径。</param>
    /// <returns>旧库备份路径；失败时为 null。</returns>
    /// <remarks>
    /// 还原只保证文件层面正确：进程内的图库集合、分类与分组仍是旧库的内存副本，
    /// 必须重启应用才能全部收敛，故调用方必须向用户明示这一点。
    /// </remarks>
    public async Task<string?> RestoreSnapshotAsync(string snapshotPath)
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;

        try
        {
            var backupPath = await _snapshots.RestoreSnapshotAsync(snapshotPath).ConfigureAwait(true);
            SnapshotStatusText = $"已还原；旧库保留在 {backupPath}，请重启应用。";
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                      or ArgumentException or InvalidOperationException)
        {
            SnapshotStatusText = $"还原失败：{ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 清理过期的旧库副本（整库还原产生的 .bak-* 文件）；启动时调用一次。
    /// </summary>
    /// <returns>删除的文件数。</returns>
    /// <remarks>每个副本都是整库体积，长期累积会明显占用磁盘，故不留给用户手工清理。</remarks>
    public int CleanupObsoleteBackups() => _snapshots.CleanupObsoleteBackups(KeepBackupCopies, MaxBackupAge);

    /// <summary>刷新同步状态说明（进入设置页与每次设置变更后调用）。</summary>
    public void RefreshSyncStatus() => UpdateSyncStatusText();

    /// <summary>应用同步开关并落盘。</summary>
    /// <param name="enabled">是否启用。</param>
    public async Task SetSyncEnabledAsync(bool enabled)
    {
        await _settings.SaveAsync(_settings.Current with { BackupSyncEnabled = enabled }).ConfigureAwait(true);
        OnPropertyChanged(nameof(IsSyncEnabled));
        UpdateSyncStatusText();
    }

    /// <summary>应用同步周期并落盘。</summary>
    /// <param name="frequency">同步周期。</param>
    public async Task SetSyncFrequencyAsync(BackupSyncFrequency frequency)
    {
        await _settings.SaveAsync(_settings.Current with { BackupSyncFrequency = frequency }).ConfigureAwait(true);
        OnPropertyChanged(nameof(SyncFrequencyIndex));
        UpdateSyncStatusText();
    }

    /// <summary>立即执行一次同步；成功时推进上次同步时间，失败只更新状态文案。</summary>
    /// <returns>同步结果。</returns>
    public async Task<BackupSyncResult?> SyncNowAsync()
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;

        try
        {
            var result = await _sync.SyncAsync(_settings.Current).ConfigureAwait(true);

            if (result.Succeeded)
            {
                // 只有成功才推进时间戳：失败后下一次启动仍会补做，避免周期内静默放弃。
                await _settings.SaveAsync(_settings.Current with { BackupSyncLastUtc = result.CompletedUtc })
                    .ConfigureAwait(true);
            }

            SyncStatusText = result.Succeeded
                ? $"已同步到 {result.TargetPath}"
                : $"同步失败：{result.ErrorMessage}";

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 启动时按设置检查同步周期，超期则补做一次；手动频率与未启用时只刷新状态文案。
    /// </summary>
    /// <remarks>
    /// 应用不是常驻进程，「每天 / 每周 / 每月」无法靠定时器实现——只能启动时判一次是否超期，
    /// 补做错过的周期。调用方须 fire-and-forget 且不阻塞启动。
    /// </remarks>
    public async Task RunStartupSyncIfDueAsync()
    {
        if (!_sync.IsDue(_settings.Current, DateTimeOffset.UtcNow))
        {
            UpdateSyncStatusText();
            return;
        }

        await SyncNowAsync().ConfigureAwait(true);
    }

    /// <summary>按当前设置刷新状态文案：未启用 / 未探测到目录 / 目标目录与上次同步时间。</summary>
    private void UpdateSyncStatusText()
    {
        var current = _settings.Current;

        if (!current.BackupSyncEnabled)
        {
            SyncStatusText = "未启用；启用后每次启动应用时检查并同步。";
            return;
        }

        if (_sync.ResolveTargetFolder(current) is not { } target)
        {
            SyncStatusText = "未检测到 OneDrive：请安装并登录 OneDrive 后重试。";
            return;
        }

        var last = current.BackupSyncLastUtc is { } value
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "尚未同步";

        SyncStatusText = $"目标：{target}；上次同步：{last}";
    }

    /// <summary>是否有导出或导入正在进行。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
            }
        }
    }

    /// <summary>两个按钮的可用性：导入导出期间禁用，避免并发改写同一批数据。</summary>
    public bool CanInteract => !IsBusy;

    /// <summary>导出用户数据到指定路径。</summary>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <returns>是否导出成功。</returns>
    public async Task<bool> ExportAsync(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;

        try
        {
            var counts = await _backup.ExportAsync(destinationPath, _settings.Current.MusicLibraryPaths)
                .ConfigureAwait(true);

            ExportStatusText = $"已导出 {counts.Favorites} 条收藏、{counts.Groups} 个分组、"
                + $"{counts.Categories} 个分类、{counts.Rules} 条规则、{counts.Folders} 个扫描源。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                      or InvalidOperationException)
        {
            // 目标目录被占用（OneDrive 正在同步）或权限不足是最常见的失败原因，如实呈现。
            ExportStatusText = $"导出失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>导入用户数据；成功时刷新各视图模型并返回结果，失败返回 null。</summary>
    /// <param name="sourcePath">备份文件路径。</param>
    /// <param name="importItemCategories">是否应用条目的分类归属。</param>
    /// <param name="pathMappings">路径前缀重映射（旧根 → 新根）；为空表示不做改写。</param>
    /// <returns>导入结果；失败时为 null（原因写入 <see cref="ImportStatusText"/>）。</returns>
    public async Task<UserDataImportResult?> ImportAsync(
        string sourcePath,
        bool importItemCategories,
        IReadOnlyList<PathPrefixMapping>? pathMappings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;

        try
        {
            var options = new UserDataImportOptions
            {
                ImportItemCategories = importItemCategories,
                PathMappings = pathMappings ?? []
            };

            var result = await _backup.ImportAsync(sourcePath, options).ConfigureAwait(true);

            await ApplyMusicFoldersAsync(result.MusicFolders).ConfigureAwait(true);
            await RefreshViewsAsync().ConfigureAwait(true);

            ImportStatusText = BuildImportSummary(result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                      or InvalidOperationException or FormatException)
        {
            ImportStatusText = $"导入失败：{ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把备份中的音乐目录与本地列表合并去重后落盘；无新增时不写盘。</summary>
    private async Task ApplyMusicFoldersAsync(IReadOnlyList<string> fromBackup)
    {
        if (fromBackup.Count == 0)
        {
            return;
        }

        var merged = new List<string>(_settings.Current.MusicLibraryPaths);

        foreach (var path in fromBackup)
        {
            if (!string.IsNullOrWhiteSpace(path)
                && !merged.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(path);
            }
        }

        if (merged.Count == _settings.Current.MusicLibraryPaths.Count)
        {
            return;
        }

        await _settings.SaveAsync(_settings.Current with { MusicLibraryPaths = merged }).ConfigureAwait(true);
        await _settingsViewModel.LoadMusicFoldersAsync().ConfigureAwait(true);
    }

    /// <summary>导入后刷新受影响的数据源；侧栏子项随集合变更事件自动重建。</summary>
    private async Task RefreshViewsAsync()
    {
        await _settingsViewModel.LoadAsync().ConfigureAwait(true);
        await _categories.LoadAsync().ConfigureAwait(true);
        await _favoriteGroups.LoadAsync().ConfigureAwait(true);
        await _gallery.ReloadAsync().ConfigureAwait(true);
    }

    private static string BuildImportSummary(UserDataImportResult result)
    {
        var summary = $"已导入：新增 {result.Added} 项、更新 {result.Updated} 项、"
            + $"收藏 {result.Favorites} 条。";

        if (result.Unmatched > 0)
        {
            summary += $" 有 {result.Unmatched} 个文件在当前库中未找到（已被移动或删除），"
                + "其收藏与分组归属未能写入。";
        }

        if (result.SkippedRules > 0)
        {
            summary += $" 跳过 {result.SkippedRules} 条无效规则。";
        }

        return summary;
    }
}
