/**
 * 媒体元数据探测的数据载体（M3）。
 * 职责：承载单个媒体文件探测出的尺寸与时长，以及回填时提交给仓储的更新内容。
 * 复用约定：探测结果为不可变 record，供后台回填服务与界面预取共用同一份数据结构；
 *          探测状态以整数枚举持久化，禁止在仓储层使用魔法数字。
 * 关键约束：探测失败与「格式不支持」必须置为 Failed 而非保持 Pending，否则坏文件会被无限重试、
 *          永远占满每一批的额度、把真正的待处理条目挤在后面；
 *          宽高必须已计入 EXIF 方向与视频旋转标记，否则宽高比会反过来，比不探测更糟。
 */

namespace Pixbian.Core.Models;

/// <summary>媒体条目的元数据探测状态。</summary>
public enum MediaMetadataState
{
    /// <summary>尚未探测。</summary>
    Pending = 0,

    /// <summary>探测成功，宽高已落库。</summary>
    Completed = 1,

    /// <summary>探测失败；不再重试。</summary>
    Failed = 2
}

/// <summary>单个媒体文件的元数据探测结果。</summary>
/// <param name="Width">像素宽度；未探测到时为 null。</param>
/// <param name="Height">像素高度；未探测到时为 null。</param>
/// <param name="DurationMs">视频时长（毫秒）；图片为 null。</param>
public sealed record MediaMetadataProbeResult(int? Width, int? Height, long? DurationMs);

/// <summary>提交给仓储的单条元数据更新。</summary>
/// <param name="Id">条目主键。</param>
/// <param name="Result">探测结果；为 null 表示探测失败，状态置为 Failed。</param>
/// <param name="ProbedUtc">探测完成时间（UTC）。</param>
public sealed record MediaMetadataUpdate(long Id, MediaMetadataProbeResult? Result, DateTimeOffset ProbedUtc);
