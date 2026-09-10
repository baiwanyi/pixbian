/**
 * 备份文件名规则（本机导出与 OneDrive 同步共用）。
 * 职责：统一 backup-pixbian-userdata-<yyyyMMdd-HHmmss>.json 与
 *      backup-pixbian-index-<yyyyMMdd-HHmmss>.db 两种文件名的生成与识别。
 * 复用约定：时间戳一律取本地时间——用户按文件名找备份时看的是本地时钟，而非 UTC；
 *          本机导出的默认文件名同样取自本类，避免两处各写一份前缀而逐渐漂移。
 * 关键约束：同一次备份的两份文件共用同一时间戳（成对出现），按时间戳识别即可成组清理，
 *          否则目录里会残留无法配对的孤儿文件。
 */

using System.Globalization;

namespace Pixbian.Core.Services;

/// <summary>备份文件名规则。</summary>
public static class BackupFileNaming
{
    /// <summary>用户数据备份文件前缀。</summary>
    public const string UserDataPrefix = "backup-pixbian-userdata-";

    /// <summary>索引库快照文件前缀。</summary>
    public const string IndexPrefix = "backup-pixbian-index-";

    /// <summary>用户数据备份扩展名。</summary>
    public const string UserDataExtension = ".json";

    /// <summary>索引库快照扩展名。</summary>
    public const string IndexExtension = ".db";

    /// <summary>文件名中的时间戳格式（与目录里能看到的备份名一致）。</summary>
    public const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>时间戳长度：8 位日期 + 1 位分隔符 + 6 位时间。</summary>
    private const int StampLength = 15;

    /// <summary>时间戳里分隔符的位置。</summary>
    private const int StampSeparatorIndex = 8;

    /// <summary>把本地时间格式化为文件名时间戳。</summary>
    /// <param name="localTime">本地时间。</param>
    /// <returns>形如 20260910-194105 的时间戳。</returns>
    public static string FormatStamp(DateTimeOffset localTime) =>
        localTime.ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>按时间戳生成用户数据备份的主文件名（不含扩展名，供文件选择器预填）。</summary>
    /// <param name="stamp">时间戳。</param>
    /// <returns>形如 backup-pixbian-userdata-20260910-194105 的文件名。</returns>
    public static string BuildUserDataBaseName(string stamp) => $"{UserDataPrefix}{stamp}";

    /// <summary>按时间戳生成索引库快照的主文件名（不含扩展名，供文件选择器预填）。</summary>
    /// <param name="stamp">时间戳。</param>
    /// <returns>形如 backup-pixbian-index-20260910-194105 的文件名。</returns>
    public static string BuildIndexBaseName(string stamp) => $"{IndexPrefix}{stamp}";

    /// <summary>按时间戳生成用户数据备份文件名。</summary>
    /// <param name="stamp">时间戳。</param>
    /// <returns>形如 backup-pixbian-userdata-20260910-194105.json 的文件名。</returns>
    public static string BuildUserDataFileName(string stamp) =>
        $"{BuildUserDataBaseName(stamp)}{UserDataExtension}";

    /// <summary>按时间戳生成索引库快照文件名。</summary>
    /// <param name="stamp">时间戳。</param>
    /// <returns>形如 backup-pixbian-index-20260910-194105.db 的文件名。</returns>
    public static string BuildIndexFileName(string stamp) =>
        $"{BuildIndexBaseName(stamp)}{IndexExtension}";

    /// <summary>从文件名反解时间戳。</summary>
    /// <param name="fileName">文件名（不含目录）。</param>
    /// <returns>时间戳；不符合命名规则时为 null。</returns>
    public static string? TryGetStamp(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        // 两种前缀与扩展名的组合都要认：目录里可能有 pair 中只剩一个文件的情况。
        foreach (var (prefix, extension) in new[]
                 {
                     (UserDataPrefix, UserDataExtension),
                     (IndexPrefix, IndexExtension)
                 })
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                var stamp = fileName[prefix.Length..^extension.Length];

                if (IsStamp(stamp))
                {
                    return stamp;
                }
            }
        }

        return null;
    }

    /// <summary>时间戳必须是定长的日期与时间数字，避免把用户手工改名的文件误当成备份。</summary>
    private static bool IsStamp(string value)
    {
        if (value.Length != StampLength || value[StampSeparatorIndex] != '-')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (index != StampSeparatorIndex && !char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
