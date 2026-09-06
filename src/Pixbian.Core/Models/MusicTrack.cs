/**
 * 音乐曲目领域模型：一条记录对应音乐库目录中的一个受支持的音频文件。
 * 职责：承载音乐库索引的基础属性，作为短片页背景音乐候选池的唯一数据载体。
 * 复用约定：与 media_items 解耦为独立表，避免音频混入图库语义；
 *          Path 为业务唯一键，AddedUtc 为 UTC 并以 ISO8601 往返格式存取。
 * 关键约束：本模型不参与图库索引、缩略图解码与元数据回填，仅供短片页随机抽曲使用。
 */

namespace Pixbian.Core.Models;

/// <summary>音乐曲目。</summary>
public sealed record MusicTrack
{
    /// <summary>文件完整路径，业务唯一键。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>文件名（含扩展名）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>父目录完整路径，用于按音乐库目录对账。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long FileSize { get; init; }

    /// <summary>入库时间（UTC）。</summary>
    public DateTimeOffset AddedUtc { get; init; }
}
