/**
 * 缩略图加载状态（图库条目展示）。
 * 职责：区分「尚未加载」「已加载」「加载失败」三种展示形态，供 ThumbnailPresenter 驱动
 *      骨架屏、缩略图与错误占位之间的切换。
 * 复用约定：状态由 MediaItemViewModel 依据缩略图加载结果写入，控件只读不写。
 * 关键约束：取消**不属于失败**。滚出视口的取消（CancelPendingLoad）与切换条目时的取消都是
 *          高频正常路径，必须回落到 Loading 而非 Failed，否则界面会随机冒出错误占位图标。
 */

namespace Pixbian.Controls;

/// <summary>缩略图加载状态。</summary>
public enum ThumbnailLoadState
{
    /// <summary>尚未加载或正在加载，展示骨架屏。</summary>
    Loading,

    /// <summary>已成功加载，展示缩略图。</summary>
    Loaded,

    /// <summary>加载失败（格式不受支持、文件被占用等），展示错误占位。</summary>
    Failed,

    /// <summary>文件已不存在（被移动或删除）：占位层额外展示文件名与完整路径，供用户定位。</summary>
    Missing
}
