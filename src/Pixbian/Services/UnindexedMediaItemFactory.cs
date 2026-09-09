/**
 * 未入库媒体条目工厂。
 * 职责：按磁盘文件构造仅供查看器与播放器使用的临时条目（主键 0、不写索引库），
 *      供文件激活等不属于任何媒体库的入口使用（例如资源管理器中双击打开的图片）。
 * 复用约定：类型判定统一走 MediaFileClassifier，调用方只负责传路径与判空；
 *          文件不存在或不可访问一律返回 null，由调用方静默返回，不向上抛异常。
 * 关键约束：条目不得写入索引库——双击的文件未必属于任何媒体库，写入会污染库并触发重复探测；
 *          拍摄时间取创建时间与修改时间的较早者，仅为排序与展示提供兜底值，非 EXIF 拍摄时间。
 */

using System;
using System.IO;
using Pixbian.Core.Models;
using Pixbian.Core.Services;

namespace Pixbian.Services;

/// <summary>未入库媒体条目工厂。</summary>
public static class UnindexedMediaItemFactory
{
    /// <summary>按磁盘文件构造未入库条目；文件不存在或不可访问时返回 null。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    public static MediaItem? Create(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                return null;
            }

            return new MediaItem
            {
                Path = info.FullName,
                FileName = info.Name,
                Directory = info.DirectoryName ?? string.Empty,
                Kind = MediaFileClassifier.Classify(info.FullName),
                FileSize = info.Length,
                CreatedUtc = info.CreationTimeUtc,
                ModifiedUtc = info.LastWriteTimeUtc,
                IndexedUtc = DateTimeOffset.UtcNow,
                TakenUtc = info.CreationTimeUtc < info.LastWriteTimeUtc
                    ? info.CreationTimeUtc
                    : info.LastWriteTimeUtc
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
