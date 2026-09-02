/**
 * 应用数据目录路径模块。
 * 职责：集中定义索引数据库、缩略图缓存、设置文件与日志的存放位置，避免路径散落各处。
 * 复用约定：全部基于 LocalApplicationData，与 README 声明的存储位置保持一致；
 *          所有文件访问都必须经本模块取路径，禁止在业务代码中硬编码目录名。
 * 关键约束：目录必须在使用前经 EnsureCreated 创建，否则 SQLite 与文件写入会失败；
 *          数据库属用户隐私数据（含本地文件索引），严禁放置到可同步到版本库的目录。
 */

using System.IO;

namespace Pixbian.Core.Utilities;

/// <summary>应用数据目录。</summary>
public static class AppPaths
{
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

    /// <summary>创建全部数据目录。</summary>
    public static void EnsureCreated()
    {
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
}
