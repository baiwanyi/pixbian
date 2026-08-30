/**
 * 应用数据目录路径模块。
 * 职责：集中定义索引数据库、缩略图缓存、设置文件与日志的存放位置，避免路径散落各处。
 * 复用约定：全部基于 LocalApplicationData，与 README 声明的存储位置保持一致；
 *          所有文件访问都必须经本模块取路径，禁止在业务代码中硬编码目录名。
 * 关键约束：目录必须在使用前经 EnsureCreated 创建，否则 SQLite 与文件写入会失败；
 *          数据库属用户隐私数据（含本地文件索引），严禁放置到可同步到版本库的目录；
 *          品牌由 PhotoApps 更名为 Pixbian 后，旧数据目录须经自动迁移承接，避免用户重新扫描全库。
 */

using System.IO;

namespace Pixbian.Core.Utilities;

/// <summary>应用数据目录。</summary>
public static class AppPaths
{
    /// <summary>旧版数据目录名，仅用于一次性迁移。</summary>
    private const string LegacyDirectoryName = "PhotoApps";

    private static bool _isLegacyMigrated;

    /// <summary>应用数据根目录。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pixbian");

    /// <summary>索引数据库文件路径。</summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "index.db");

    /// <summary>缩略图磁盘缓存目录。</summary>
    public static string ThumbnailCacheDirectory => Path.Combine(DataDirectory, "Thumbs");

    /// <summary>用户设置文件路径。</summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>日志文件目录。</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

    /// <summary>创建全部数据目录，并在首次调用时承接旧版目录中的数据。</summary>
    public static void EnsureCreated()
    {
        MigrateLegacyDirectory();

        foreach (var directory in new[]
                 {
                     DataDirectory,
                     ThumbnailCacheDirectory,
                     LogDirectory
                 })
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    /// <summary>
    /// 把旧版数据目录整体搬迁到当前目录。同卷 Directory.Move 为原子重命名，
    /// 故此处无需复制文件；迁移失败不阻断启动，降级为使用空的新目录。
    /// </summary>
    private static void MigrateLegacyDirectory()
    {
        if (_isLegacyMigrated)
        {
            return;
        }

        _isLegacyMigrated = true;

        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyDirectoryName);

        if (!Directory.Exists(legacyDirectory) || Directory.Exists(DataDirectory))
        {
            return;
        }

        try
        {
            Directory.Move(legacyDirectory, DataDirectory);
        }
        catch (IOException)
        {
            // 旧目录被占用或已不存在：保持新目录为空，下次启动再试。
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限搬迁：保持新目录为空，避免启动失败。
        }
    }
}
