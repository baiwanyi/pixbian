/**
 * 幻灯片放映独立窗口。
 * 职责：以独立顶层窗口承载幻灯片放映页，窗口形态（无边框铺满显示器、直角贴边、置顶）
 *      由 FullscreenWindowBase 承担；舞台为纯黑不透明（由页面根承担），不用亚克力背景。
 * 复用约定：放映页由依赖注入提供（单例，跨窗口复用），窗口自身为瞬态；
 *          同一时刻至多一个放映窗口，主窗口经引用跟踪复用未关闭实例；
 *          关闭统一经 Close() 收口，Closed 时摘除内容以触发页面 Unloaded 清理（停放映计时）。
 * 关键约束：置顶保证放映期间稳定压住任务栏；关闭窗口即结束放映，无最小化态。
 */

using System;
using Microsoft.UI.Xaml;

namespace Pixbian.Views;

/// <summary>幻灯片放映独立窗口。</summary>
public sealed class SlideShowWindow : FullscreenWindowBase
{
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
}
