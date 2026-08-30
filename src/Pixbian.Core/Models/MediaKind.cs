/**
 * 媒体类型枚举定义模块。
 * 职责：统一图片、视频及其他文件在索引、筛选与展示层的类型标识。
 * 复用约定：所有仓储读写均以整数形式持久化，避免字符串比较开销；判定依据由 MediaFileClassifier 统一产出。
 * 关键约束：枚举数值一旦发布不得重新排序或复用，否则将破坏既有索引库中的类型判别。
 */

namespace Pixbian.Core.Models;

/// <summary>媒体文件类型。</summary>
public enum MediaKind
{
    /// <summary>图片（JPEG / PNG / GIF / WebP / HEIC / RAW 等）。</summary>
    Image = 0,

    /// <summary>视频（MP4 / MOV / AVI / MKV 等）。</summary>
    Video = 1,

    /// <summary>其他受支持但无法归类的文件。</summary>
    Other = 2
}
