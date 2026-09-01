/**
 * 用户设置与媒体查询模型（M2）。
 * 职责：承载界面偏好（主题、视图模式、缩略图尺寸）与媒体库查询条件（类型、搜索、排序、分页）。
 * 复用约定：主题使用本项目的 AppTheme 枚举而非 WinUI 的 ElementTheme，以保持领域层不依赖 UI 框架；
 *          两者在界面层做映射，领域层与持久化层只认 AppTheme。
 * 关键约束：ThumbnailSize 限定为预设档位，任意值会导致缓存键爆炸并失去复用效果；
 *          MediaQuery 为不可变记录，构造后不得修改，避免查询过程中条件漂移。
 */

namespace Pixbian.Core.Models;

/// <summary>应用主题。</summary>
public enum AppTheme
{
    /// <summary>跟随系统。</summary>
    System = 0,

    /// <summary>浅色。</summary>
    Light = 1,

    /// <summary>深色。</summary>
    Dark = 2
}

/// <summary>图库视图模式。</summary>
public enum GalleryViewMode
{
    /// <summary>缩略图网格。</summary>
    Grid = 0,

    /// <summary>自适应行式布局（按纵横比排满每行）。保留历史数值 2，旧配置无需迁移。</summary>
    Justified = 2
}

/// <summary>排序依据。</summary>
public enum MediaSortKey
{
    /// <summary>随机顺序；无升降序语义，方向被忽略。</summary>
    Random = 0,

    /// <summary>修改日期。</summary>
    ModifiedDate = 1,

    /// <summary>文件大小。</summary>
    FileSize = 2,

    /// <summary>文件名。</summary>
    FileName = 3
}

/// <summary>排序方向。</summary>
public enum SortDirection
{
    /// <summary>升序。</summary>
    Ascending = 0,

    /// <summary>降序。</summary>
    Descending = 1
}

/// <summary>用户设置。</summary>
public sealed record AppSettings
{
    /// <summary>界面主题。</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>图库视图模式。</summary>
    public GalleryViewMode ViewMode { get; init; } = GalleryViewMode.Justified;

    /// <summary>缩略图边长（像素），须取 ThumbnailSizes 中的预设档位。</summary>
    public int ThumbnailSize { get; init; } = ThumbnailSizes.Default;

    /// <summary>幻灯片播放间隔（秒）。</summary>
    public int SlideShowIntervalSeconds { get; init; } = 5;

    /// <summary>是否启用局域网 Web 访问。</summary>
    public bool IsWebSharingEnabled { get; init; }

    /// <summary>Web 服务监听端口。</summary>
    public int WebSharingPort { get; init; } = 8756;

    /// <summary>
    /// Web 访问密码的 PBKDF2 哈希（AuthService.HashPassword 的输出格式）。
    /// 关键约束：只存哈希不存明文；为空表示无需密码即可访问（仅限可信局域网）。
    /// </summary>
    public string? WebPasswordHash { get; init; }
}

/// <summary>缩略图尺寸预设档位。</summary>
public static class ThumbnailSizes
{
    /// <summary>可选档位（等高视图即图片固定高度，网格视图为格子边长），从小到大，与视图菜单「小 / 中等 / 大」一一对应。</summary>
    public static IReadOnlyList<int> Presets { get; } = [128, 256, 512];

    /// <summary>默认档位。取 256 以在高 DPI 屏与常规显示下均保持清晰，且不显著增加内存占用。</summary>
    public const int Default = 256;

    /// <summary>
    /// 解码档位（物理像素，按位图最长边量化）。
    /// 请求尺寸一律向上取到本序列中最近的档位：档位过密会让内存缓存条目膨胀，
    /// 过疏则档位远超显示尺寸，位图被显示端缩小后发虚——WinUI 3 的 Image 没有 mipmap，
    /// 缩小只能靠双线性采样，缩放比越大细节损失越明显。
    /// 相邻档位比值控制在 1.33 以内，最坏情况位图比显示区大 33%，缩小 33% 肉眼无损。
    /// </summary>
    public static IReadOnlyList<int> DecodeBuckets { get; } =
        [128, 160, 192, 224, 256, 320, 384, 448, 512, 640, 768, 896, 1024, 1280, 1536, 2048, 2560];

    /// <summary>把目标边长向上量化到解码档位；非法值与超出最大档位的取值分别回落到默认档位与最大档位。</summary>
    /// <param name="size">目标边长（逻辑像素）。</param>
    /// <returns>量化后的边长。</returns>
    public static int SnapToBucket(double size)
    {
        if (double.IsNaN(size) || size <= 0)
        {
            return Default;
        }

        foreach (var bucket in DecodeBuckets)
        {
            if (bucket >= size)
            {
                return bucket;
            }
        }

        return DecodeBuckets[^1];
    }
}

/// <summary>媒体查询条件。</summary>
public sealed record MediaQuery
{
    /// <summary>媒体类型；为 null 时不限。</summary>
    public MediaKind? Kind { get; init; }

    /// <summary>分类主键；为 null 时不限。</summary>
    public long? CategoryId { get; init; }

    /// <summary>是否仅收藏条目；为 null 时不限。</summary>
    public bool? IsFavorite { get; init; }

    /// <summary>文件名搜索关键词；为 null 或空白时不参与筛选。</summary>
    public string? SearchText { get; init; }

    /// <summary>目录完整路径；为 null 或空白时不参与筛选，命中时含该目录自身与其全部子目录。</summary>
    public string? DirectoryPath { get; init; }

    /// <summary>排序依据。</summary>
    public MediaSortKey SortKey { get; init; } = MediaSortKey.ModifiedDate;

    /// <summary>排序方向；SortKey 为 Random 时不参与排序。</summary>
    public SortDirection SortDirection { get; init; } = SortDirection.Descending;

    /// <summary>
    /// 随机排序种子，仅 SortKey 为 Random 时生效。
    /// 关键约束：随机顺序必须由固定种子生成而非 SQL 的 RANDOM()，否则增量分页会重复或漏掉条目；
    /// 同一种子下顺序稳定，重新选择「随机」才换种子。
    /// </summary>
    public int RandomSeed { get; init; }

    /// <summary>跳过的条目数，用于分页。</summary>
    public int Skip { get; init; }

    /// <summary>取回的条目数。</summary>
    public int Take { get; init; } = 200;
}
