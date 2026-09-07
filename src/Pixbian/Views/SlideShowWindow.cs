/**
 * 幻灯片放映独立窗口。
 * 职责：以独立顶层窗口承载幻灯片放映页，窗口形态（无边框铺满显示器、直角贴边、置顶）
 *      由 FullscreenWindowBase 承担；窗口态下内容延伸进标题栏，顶部拖动区按页面
 *      浮动按钮布局精确划定（避开顶部居中工具栏）；舞台为纯黑不透明
 *      （由页面根承担），不用亚克力背景。
 * 复用约定：放映页由依赖注入提供（单例，跨窗口复用），窗口自身为瞬态；
 *          同一时刻至多一个放映窗口，主窗口经引用跟踪复用未关闭实例；
 *          关闭统一经 Close() 收口，Closed 时摘除内容以触发页面 Unloaded 清理（停放映计时）。
 * 关键约束：置顶保证放映期间稳定压住任务栏；关闭窗口即结束放映，无最小化态。
 */

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Pixbian.Views;

/// <summary>幻灯片放映独立窗口。</summary>
public sealed class SlideShowWindow : FullscreenWindowBase
{
    /// <summary>顶部居中工具栏的半宽安全余量（逻辑像素）。</summary>
    private const double ToolbarHalfWidth = 170;

    /// <summary>顶部拖动区高度（逻辑像素），与基类窗口态默认拖区一致。</summary>
    private const double DragHeight = 44;

    /// <summary>初始化幻灯片放映窗口：无边框铺满所在显示器，黑色不透明舞台。</summary>
    /// <param name="page">放映页，由依赖注入提供。</param>
    public SlideShowWindow(SlideShowPage page) : base(isAlwaysOnTop: true)
    {
        ArgumentNullException.ThrowIfNull(page);

        Page = page;
        page.Owner = this;
        Content = page;
        Title = "幻灯片放映";

        // Closed 时摘除内容：页面脱离视觉树触发 Unloaded 清理，并为下次复用单例页解除引用。
        Closed += (_, _) => Content = null;
    }

    /// <summary>承载的放映页（单例，跨窗口复用）。</summary>
    public SlideShowPage Page { get; }

    /// <summary>
    /// 按页面内容宽度精确更新窗口态的顶部拖动区：顶部工具栏两侧的两段空隙
    /// 承担拖动 / 双击最大化，工具栏自身区域保持客户区命中（可点击）。
    /// 全屏态（标题栏不延伸）无拖动区概念，直接忽略。
    /// </summary>
    /// <param name="contentWidth">页面内容宽度（逻辑像素）。</param>
    /// <param name="rasterizationScale">光栅化缩放比（逻辑 → 物理像素）。</param>
    public void UpdateTitleBarDragRegions(double contentWidth, double rasterizationScale)
    {
        if (!IsFullscreen || !ExtendsContentIntoTitleBar)
        {
            return;
        }

        var scale = rasterizationScale > 0 ? rasterizationScale : 1.0;
        var half = contentWidth * scale / 2;
        var toolbarHalf = ToolbarHalfWidth * scale;
        var systemLeft = contentWidth * scale - SystemButtonReserve * scale;

        var rects = new List<RectInt32>();

        var leftWidth = half - toolbarHalf;
        if (leftWidth > 0)
        {
            rects.Add(new RectInt32(0, 0, (int)leftWidth, (int)(DragHeight * scale)));
        }

        var rightWidth = systemLeft - (half + toolbarHalf);
        if (rightWidth > 0)
        {
            rects.Add(new RectInt32((int)(half + toolbarHalf), 0, (int)rightWidth, (int)(DragHeight * scale)));
        }

        AppWindow.TitleBar.SetDragRectangles(rects.ToArray());
    }
}
