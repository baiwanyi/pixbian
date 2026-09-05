/**
 * 用户设置与媒体查询模型（M2）。
 * 职责：承载界面偏好（主题、视图模式、缩略图尺寸、幻灯片与图片查看器行为）与媒体库查询条件
 *      （类型、搜索、排序、分页）。
 * 复用约定：主题使用本项目的 AppTheme 枚举而非 WinUI 的 ElementTheme，以保持领域层不依赖 UI 框架；
 *          两者在界面层做映射，领域层与持久化层只认 AppTheme。
 * 关键约束：ThumbnailSize 限定为预设档位，任意值会导致缓存键爆炸并失去复用效果；
 *          幻灯片间隔与切换方式只在此处定义数值边界，落盘前的钳制由 JsonSettingsService.Normalize 统一把关，
 *          避免界面层与持久化层各写一套范围判断而互相打架；
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

/// <summary>幻灯片切换图片时的视觉过渡方式。</summary>
public enum SlideShowTransitionMode
{
    /// <summary>水平滑动：旧图向左退出，新图自右进入。</summary>
    Slide = 0,

    /// <summary>交叉淡入淡出：旧图淡出的同时新图淡入。</summary>
    Fade = 1
}

/// <summary>幻灯片播放时选取下一张的顺序。</summary>
public enum SlideShowPlayOrder
{
    /// <summary>按播放列表顺序依次前进，播到最后一张即停止。</summary>
    List = 0,

    /// <summary>随机顺序：一轮内每张只播一次，全部播完后重新洗牌继续（循环播放）。</summary>
    Random = 1
}

/// <summary>图片查看器中鼠标滚轮的行为。</summary>
public enum ViewerWheelMode
{
    /// <summary>滚轮缩放图片（Ctrl + 滚轮同样缩放）。</summary>
    Zoom = 0,

    /// <summary>滚轮切换上一张 / 下一张；Ctrl + 滚轮仍为缩放。</summary>
    Navigate = 1
}

/// <summary>图片打开时的初始缩放方式。</summary>
public enum ViewerInitialZoom
{
    /// <summary>缩放以适应窗口。</summary>
    FitToWindow = 0,

    /// <summary>按 100% 实际像素显示。</summary>
    ActualSize = 1
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

    /// <summary>幻灯片播放间隔（秒），有效范围 1–3600，越界值在持久化时被钳制。</summary>
    public int SlideShowIntervalSeconds { get; init; } = 5;

    /// <summary>图片查看器中鼠标滚轮的行为。</summary>
    public ViewerWheelMode ViewerWheelMode { get; init; } = ViewerWheelMode.Zoom;

    /// <summary>图片打开时的初始缩放方式。</summary>
    public ViewerInitialZoom ViewerInitialZoom { get; init; } = ViewerInitialZoom.FitToWindow;

    /// <summary>幻灯片播放时选取下一张的顺序。</summary>
    public SlideShowPlayOrder SlideShowOrder { get; init; } = SlideShowPlayOrder.List;

    /// <summary>幻灯片切换图片时的过渡方式。</summary>
    public SlideShowTransitionMode SlideShowTransition { get; init; } = SlideShowTransitionMode.Slide;

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

    /// <summary>随机排序的游标（random_rank 起点，含端点）。仅 SortKey 为 Random 且有值时生效：
    /// 查询走「rank &gt;= 游标」的索引范围扫描，替代 OFFSET 深翻；为 null 时保持
    /// 既有 OFFSET 行为（兼容旧调用方与测试）。</summary>
    public long? RandomCursor { get; init; }
}
