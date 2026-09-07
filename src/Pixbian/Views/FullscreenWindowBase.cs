/**
 * 全屏无边框窗口基类。
 * 职责：提供铺满所在显示器完整分辨率的沉浸式窗口宿主——无边框无标题栏、直角贴边、
 *      显式应用图标，并可选置顶压住任务栏；背景材质与页面内容由子类自行决定。
 * 复用约定：子类只负责设置 Content/Title/背景与关联回调，窗口形态初始化全部收口在基类构造；
 *          显示器矩形经 Win32 MonitorFromWindow + GetMonitorInfo 查询，多显示器精确。
 * 关键约束：unpackaged 应用的窗口图标必须经 AppWindow.SetIcon 显式设置（仅支持 .ico）；
 *          SetBorderAndTitleBar 后窗口仍残留 WS_CAPTION | WS_THICKFRAME 样式位，
 *          DWM 据此保留约 8px 隐形 resize 边距，必须清理样式位并强制重算框架才能真贴边；
 *          Win11 会给一切窗口默认画圆角，全屏画布必须经 DWM 属性禁用（DWMWCP_DONOTROUND）。
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Pixbian.Views;

/// <summary>全屏无边框窗口基类：贴边、直角、图标与可选置顶。</summary>
public abstract class FullscreenWindowBase : Window
{
    /// <summary>MONITOR_DEFAULTTONEAREST：取窗口所在（最近的）显示器。</summary>
    private const uint MonitorDefaultToNearest = 2;

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

    /// <summary>DWM 属性：窗口角偏好（Win11）。</summary>
    private const int DwmWindowAttributeCornerPreference = 33;

    /// <summary>窗口角偏好值：禁用圆角（直角）。</summary>
    private const int DwmWindowCornerPreferenceDoNotRound = 1;

    /// <summary>初始化沉浸式窗口：无边框铺满所在显示器、直角贴边并加载应用图标。</summary>
    /// <param name="isAlwaysOnTop">是否置顶；置顶后 z 序进入最高层，稳定压住任务栏。</param>
    protected FullscreenWindowBase(bool isAlwaysOnTop = true)
    {
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
            presenter.IsAlwaysOnTop = isAlwaysOnTop;
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

        ClearFrameStyles(hwnd);

        AppWindow.MoveAndResize(GetMonitorRect());
    }

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

    /// <summary>清理残留的标题栏与粗边框样式位并强制重算框架，消除隐形 resize 边距实现真贴边。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    private static void ClearFrameStyles(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, (nint)(style & ~(WsCaption | WsThickFrame)));
        _ = SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

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
