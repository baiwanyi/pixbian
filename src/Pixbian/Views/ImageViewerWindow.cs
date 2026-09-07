/**
 * 图片查看器独立窗口（Lightbox 画中画）。
 * 职责：以独立顶层窗口承载图片查看器页，窗口形态（无边框铺满显示器、直角贴边、置顶）
 *      由 FullscreenWindowBase 承担，本类补充 Thin 变体桌面亚克力背景材质提供
 *      近似透明的毛玻璃观感；图库主窗口保持原样，不受任何布局或呈现状态影响。
 * 复用约定：查看器页由依赖注入提供（单例，跨窗口复用），窗口自身为瞬态；
 *          同一时刻至多一个查看器窗口，主窗口经引用跟踪复用未关闭实例；
 *          关闭统一经 Close() 收口，Closed 时摘除内容以触发页面 Unloaded 清理。
 * 关键约束：页面外层背景必须保持透明，否则会盖住亚克力背景使其失效；
 *          亚克力走 SystemBackdrop 子类模式——自管 DesktopAcrylicController 直接对 Window 调
 *          AddSystemBackdropTarget 会因拆分投影的接口类型不一致而 CS1503，
 *          必须经基类 OnTargetConnected 传入的框架目标接入；
 *          DesktopAcrylic 在系统关闭透明效果时自动退化为纯色，不影响功能。
 */

using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Pixbian.Views;

/// <summary>图片查看器独立窗口。</summary>
public sealed class ImageViewerWindow : FullscreenWindowBase
{
    /// <summary>初始化图片查看器窗口：无边框铺满所在显示器，背景为 Thin 桌面亚克力。</summary>
    /// <param name="viewerPage">查看器页，由依赖注入提供。</param>
    public ImageViewerWindow(ImageViewerPage viewerPage) : base(isAlwaysOnTop: true)
    {
        ArgumentNullException.ThrowIfNull(viewerPage);

        ViewerPage = viewerPage;
        viewerPage.Owner = this;
        Content = viewerPage;
        Title = "图片查看器";

        // Thin 变体桌面亚克力背景（比默认 Base 更透更轻），经 SystemBackdrop 子类接入。
        SystemBackdrop = new ThinAcrylicBackdrop(this);

        // Closed 时摘除内容：页面脱离视觉树触发 Unloaded 清理，并为下次复用单例页解除引用。
        Closed += (_, _) => Content = null;
    }

    /// <summary>承载的查看器页（单例，跨窗口复用）。</summary>
    public ImageViewerPage ViewerPage { get; }
}

/// <summary>
/// Thin 变体桌面亚克力背景：Kind 只能经自管 DesktopAcrylicController 设置
/// （DesktopAcrylicBackdrop 简易路径不暴露 Kind），而 controller 直接对 Window 调
/// AddSystemBackdropTarget 会因拆分投影的接口类型不一致而失败；
/// SystemBackdrop 子类由框架在 OnTargetConnected 传入类型正确的目标，规避该问题。
/// </summary>
internal sealed class ThinAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private readonly DesktopAcrylicController _controller = new() { Kind = DesktopAcrylicKind.Thin };
    private readonly SystemBackdropConfiguration _configuration = new();
    private readonly Window _window;

    /// <summary>创建 Thin 亚克力背景并跟踪窗口激活状态（决定材质高亮程度）。</summary>
    /// <param name="window">宿主窗口。</param>
    public ThinAcrylicBackdrop(Window window)
    {
        _window = window;
        _window.Activated += OnWindowActivated;
    }

    /// <inheritdoc />
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    /// <inheritdoc />
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        // 窗口销毁时框架自动断开：此处释放 controller，瞬态窗口多次开关不积累资源。
        _controller.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller.Dispose();
        _window.Activated -= OnWindowActivated;

        base.OnTargetDisconnected(disconnectedTarget);
    }

    /// <summary>窗口激活状态变化同步给亚克力配置。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs e) =>
        _configuration.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;

    /// <summary>满足 CA1001（持有 IDisposable 字段）；实际释放发生在 OnTargetDisconnected。</summary>
    public void Dispose() => _controller.Dispose();
}
