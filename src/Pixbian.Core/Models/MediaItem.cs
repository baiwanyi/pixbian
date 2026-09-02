/**
 * 媒体条目领域模型：一条记录对应文件系统中的一个受支持的图片或视频文件。
 * 职责：承载索引库中媒体文件的基础属性，作为跨层数据交换的唯一数据载体。
 * 复用约定：全部时间字段均为 UTC，落库时统一序列化为 ISO8601 往返格式（"O"）；
 *          Kind 由 MediaFileClassifier 依据扩展名判定，宽高、时长与拍摄时间在 M3/M4 由元数据读取器回填。
 * 关键约束：Path 是业务唯一键（对应数据库 UNIQUE 约束），Upsert 以其为冲突目标；
 *          IsFavorite、Rating、CategoryId 属于用户数据，扫描写入时禁止覆盖。
 */

namespace Pixbian.Core.Models;

/// <summary>媒体条目。</summary>
public sealed record MediaItem
{
    /// <summary>数据库主键；未入库的条目为 0。</summary>
    public long Id { get; init; }

    /// <summary>文件完整路径，业务唯一键。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>文件名（含扩展名），用于正则分类与搜索。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>父目录完整路径，用于按库目录做对账与前缀筛选。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>媒体类型。</summary>
    public MediaKind Kind { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>文件创建时间（UTC）。</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>文件最后修改时间（UTC）。</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>最近一次索引时间（UTC）。</summary>
    public DateTimeOffset IndexedUtc { get; init; }

    /// <summary>拍摄或录制时间（UTC）；元数据未解析时取创建时间与修改时间中较早的一个。</summary>
    public DateTimeOffset? TakenUtc { get; init; }

    /// <summary>像素宽度；未解析时为 null。</summary>
    public int? Width { get; init; }

    /// <summary>像素高度；未解析时为 null。</summary>
    public int? Height { get; init; }

    /// <summary>视频时长（毫秒）；图片为 null。</summary>
    public long? DurationMs { get; init; }

    /// <summary>是否已收藏；属于用户数据，扫描时不得覆盖。</summary>
    public bool IsFavorite { get; init; }

    /// <summary>所属分类主键；由正则分类规则写入，扫描时不得覆盖。</summary>
    public long? CategoryId { get; init; }

    /// <summary>用户评分（0–5）；属于用户数据，扫描时不得覆盖。</summary>
    public int Rating { get; init; }

    /// <summary>固定随机序值（由 id 经乘法哈希生成，全库稳定不变）；未回填时为 null。
    /// 随机排序按本值走索引扫描并以游标分页，仅 Data 层与随机浏览链路使用。</summary>
    public long? RandomRank { get; init; }
}
