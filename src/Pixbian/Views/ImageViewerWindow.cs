/**
 * 图片查看器独立窗口（Lightbox 画中画）。
 * 职责：以独立顶层窗口承载图片查看器页，窗口铺满所在显示器的完整分辨率（无边框无标题栏、
 *      直角贴边），并以 Thin 变体桌面亚克力为背景材质提供近似透明的毛玻璃观感；
 *      图库主窗口保持原样，不受任何布局或呈现状态影响。
 * 复用约定：查看器页由依赖注入提供（单例，跨窗口复用），窗口自身为瞬态；
 *          同一时刻至多一个查看器窗口，主窗口经引用跟踪复用未关闭实例；
 *          关闭统一经 Close() 收口，Closed 时摘除内容以触发页面 Unloaded 清理
 *          （停幻灯片、停工具栏计时）。
 * 关键约束：unpackaged 应用的窗口图标必须经 AppWindow.SetIcon 显式设置（仅支持 .ico）；
 *          无边框窗口无系统标题栏与关闭按钮，退出途径由页面保证（Esc/空白点击/工具栏按钮）；
 *          页面外层背景必须保持透明，否则会盖住亚克力背景使其失效；
 *          亚克力走 SystemBackdrop 子类模式——自管 DesktopAcrylicController 直接对 Window 调
 *          AddSystemBackdropTarget 会因拆分投影的接口类型不一致而 CS1503，
 *          必须经基类 OnTargetConnected 传入的框架目标接入；
 *          显示器矩形经 Win32 MonitorFromWindow + GetMonitorInfo 获取——
 *          当前 WASDK 投影缺失 GetAncestorOptions 枚举，DisplayArea.GetFromWindowId 不可用；
 *          DesktopAcrylic 在系统关闭透明效果时自动退化为纯色，不影响功能。
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Pixbian.Views;

/// <summary>图片查看器独立窗口。</summary>
public sealed class ImageViewerWindow : Window
{
    /// <summary>MONITOR_DEFAULTTONEAREST：取窗口所在（最近的）显示器。</summary>
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>初始化图片查看器窗口：无边框铺满所在显示器，背景为 Thin 桌面亚克力。</summary>
    /// <param name="viewerPage">查看器页，由依赖注入提供。</param>
    public ImageViewerWindow(ImageViewerPage viewerPage)
    {
        ArgumentNullException.ThrowIfNull(viewerPage);

        ViewerPage = viewerPage;
        viewerPage.Owner = this;
        Content = viewerPage;
        Title = "图片查看器";

        // unpackaged 应用标题栏/任务栏不会自动继承 exe 图标，与主窗口同一方式加载。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // 模式 C：无边框窗口铺满所在显示器的完整分辨率（视觉同全屏，本质是普通窗口，
        // 任务栏只是被盖住而非销毁）。去掉边框标题栏须走 OverlappedPresenter 的成员方法。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);

            // 普通窗口的 z 序默认在任务栏之下，任务栏被唤醒（自动隐藏触发/被点击/通知弹窗）
            // 时会画到窗口上面；置顶后 z 序进入最高层，稳定压住任务栏，直至窗口关闭。
            presenter.IsAlwaysOnTop = true;
        }

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        // Win11 会给一切窗口（含无边框）默认画圆角，四角露出桌面形同「未贴边」；
        // 全屏画布必须直角（DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_DONOTROUND）。
        var cornerPreference = DwmWindowCornerPreferenceDoNotRound;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmWindowAttributeCornerPreference,
            ref cornerPreference,
            sizeof(int));

        // SetBorderAndTitleBar 后窗口仍残留 WS_CAPTION | WS_THICKFRAME 样式位，DWM 据此
        // 保留约 8px 的隐形 resize 边距，表现为四周不贴边；直接清理样式位并强制重算框架。
        ClearFrameStyles(hwnd);

        AppWindow.MoveAndResize(GetMonitorRect());

        // Thin 变体桌面亚克力背景（比默认 Base 更透更轻），经 SystemBackdrop 子类接入。
        SystemBackdrop = new ThinAcrylicBackdrop(this);

        // Closed 时摘除内容：页面脱离视觉树触发 Unloaded 清理，并为下次复用单例页解除引用。
        Closed += (_, _) => Content = null;
    }

    /// <summary>承载的查看器页（单例，跨窗口复用）。</summary>
    public ImageViewerPage ViewerPage { get; }

    /// <summary>取本窗口所在显示器的完整矩形（含任务栏区域），经 Win32 查询，多显示器精确。</summary>
    /// <returns>显示器矩形（物理像素）。</returns>
    private RectInt32 GetMonitorRect()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);

        var info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(info);

        if (GetMonitorInfo(monitor, ref info))
        {
            return new RectInt32(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);
        }

        // 查询失败兜底：退回主显示器工作区，保证窗口始终铺满一块屏幕。
        return DisplayArea.Primary.WorkArea;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    /// <summary>GWL_STYLE 索引。</summary>
    private const int GwlStyle = -16;

    /// <summary>窗口样式位：标题栏（含系统菜单）。</summary>
    private const long WsCaption = 0x00C00000;

    /// <summary>窗口样式位：可调整大小的粗边框（DWM 据此保留隐形 resize 边距）。</summary>
    private const long WsThickFrame = 0x00040000;

    /// <summary>SetWindowPos 标志：保持位置 / 尺寸 / Z 序不变，仅强制重算非客户区框架。</summary>
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>清理残留的标题栏与粗边框样式位并强制重算框架，消除隐形 resize 边距实现真贴边。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    private static void ClearFrameStyles(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, (nint)(style & ~(WsCaption | WsThickFrame)));
        _ = SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint hwndAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    /// <summary>DWM 属性：窗口角偏好（Win11）。</summary>
    private const int DwmWindowAttributeCornerPreference = 33;

    /// <summary>窗口角偏好值：禁用圆角（直角）。</summary>
    private const int DwmWindowCornerPreferenceDoNotRound = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int sizeOfValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
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

