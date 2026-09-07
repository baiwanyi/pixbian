/**
 * 全屏无边框窗口基类。
 * 职责：提供沉浸式窗口宿主并支持「全屏 ↔ 窗口」双形态切换——全屏态无边框无标题栏、
 *      直角贴边铺满所在显示器、可选置顶压住任务栏；窗口态恢复标题栏与可调边框，
 *      预设尺寸居中于工作区并取消置顶；背景材质与页面内容由子类自行决定。
 * 复用约定：子类只负责设置 Content/Title/背景与关联回调，窗口形态初始化与切换全部收口在基类；
 *          显示器矩形经 Win32 MonitorFromWindow + GetMonitorInfo 查询，多显示器精确。
 * 关键约束：unpackaged 应用的窗口图标必须经 AppWindow.SetIcon 显式设置（仅支持 .ico）；
 *          SetBorderAndTitleBar 后窗口仍残留 WS_CAPTION | WS_THICKFRAME 样式位，
 *          DWM 据此保留约 8px 隐形 resize 边距，必须清理样式位并强制重算框架才能真贴边，
 *          切回窗口态时必须对称恢复样式位，否则标题栏出现却无法拖动与调整大小；
 *          Win11 会给一切窗口默认画圆角，全屏画布必须经 DWM 属性禁用（DWMWCP_DONOTROUND），
 *          窗口态恢复默认圆角。
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

    /// <summary>窗口角偏好值：默认（Win11 按系统规则画圆角）。</summary>
    private const int DwmWindowCornerPreferenceDefault = 0;

    /// <summary>窗口态的默认宽度（逻辑尺寸按物理像素计）；显示器过小时钳制到工作区。</summary>
    private const int WindowedWidth = 1280;

    /// <summary>窗口态的默认高度。</summary>
    private const int WindowedHeight = 800;

    /// <summary>窗口态顶部拖动区高度（逻辑像素）。</summary>
    private const double TopDragHeight = 44;

    /// <summary>窗口态右上系统按钮组的预留宽度（逻辑像素）；子类划定拖动区时复用。</summary>
    protected const double SystemButtonReserve = 150;

    /// <summary>构造时确定的演示器引用；形态切换复用同一实例。</summary>
    private readonly OverlappedPresenter? _presenter;

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
        _presenter = AppWindow.Presenter as OverlappedPresenter;

        if (_presenter is not null)
        {
            _presenter.SetBorderAndTitleBar(false, false);
            _presenter.IsAlwaysOnTop = isAlwaysOnTop;
        }

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        // Win11 会给一切窗口（含无边框）默认画圆角，四角露出桌面形同「未贴边」；
        // 全屏画布必须直角（DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_DONOTROUND）。
        SetCornerPreference(hwnd, DwmWindowCornerPreferenceDoNotRound);

        ClearFrameStyles(hwnd);

        AppWindow.MoveAndResize(GetMonitorRect());
    }

    /// <summary>当前是否处于全屏形态；构造默认全屏。</summary>
    public bool IsFullscreen { get; private set; } = true;

    /// <summary>切换全屏与窗口形态；窗口态取消置顶并恢复标题栏，全屏态重新贴边置顶。</summary>
    public void ToggleFullscreen() => SetFullscreen(!IsFullscreen);

    /// <summary>
    /// 设置窗口形态。两个方向都必须对称处理样式位：全屏态不清干净会留约 8px 隐形边距，
    /// 窗口态不恢复则标题栏显示出来却拖不动、调不了大小。
    /// </summary>
    /// <param name="fullscreen">true 切入全屏态；false 切入窗口态。</param>
    private void SetFullscreen(bool fullscreen)
    {
        if (IsFullscreen == fullscreen)
        {
            return;
        }

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        if (fullscreen)
        {
            if (_presenter is not null)
            {
                _presenter.SetBorderAndTitleBar(false, false);
                _presenter.IsAlwaysOnTop = true;
            }

            SetFrameStyles(hwnd, add: 0, remove: WsCaption | WsThickFrame);
            SetCornerPreference(hwnd, DwmWindowCornerPreferenceDoNotRound);
            ExtendsContentIntoTitleBar = false;
            AppWindow.MoveAndResize(GetMonitorRect());
        }
        else
        {
            if (_presenter is not null)
            {
                _presenter.SetBorderAndTitleBar(true, true);
                _presenter.IsAlwaysOnTop = false;
            }

            SetFrameStyles(hwnd, add: WsCaption | WsThickFrame, remove: 0);
            SetCornerPreference(hwnd, DwmWindowCornerPreferenceDefault);

            // 内容延伸进标题栏：画面铺满窗口，系统按钮透明浮在内容上（前景白色保证深底可读）；
            // 按钮前景不随主题更新，必须显式设全部四组颜色。
            ExtendsContentIntoTitleBar = true;
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.White;

            // 默认拖动区为顶部全宽，会吞掉顶部浮动按钮的点击；先按系统按钮预留宽度
            // 给一个安全拖区，页面布局完成后由放映页按顶部按钮位置精确化。
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var size = AppWindow.Size;
            var dragWidth = Math.Max(0, size.Width - (int)(SystemButtonReserve * scale));
            titleBar.SetDragRectangles(
            [
                new RectInt32(0, 0, dragWidth, (int)(TopDragHeight * scale))
            ]);

            AppWindow.MoveAndResize(GetWindowedRect());
        }

        IsFullscreen = fullscreen;
    }

    /// <summary>设置窗口角偏好的 DWM 属性。</summary>
    private static void SetCornerPreference(nint hwnd, int preference)
    {
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmWindowAttributeCornerPreference,
            ref preference,
            sizeof(int));
    }

    /// <summary>取本窗口所在显示器的完整矩形（含任务栏区域），经 Win32 查询，多显示器精确。</summary>
    /// <returns>显示器矩形（物理像素）。</returns>
    private RectInt32 GetMonitorRect() => GetMonitorRects().Monitor;

    /// <summary>取窗口态矩形：预设尺寸钳制到所在显示器工作区后居中。</summary>
    /// <returns>窗口矩形（物理像素）。</returns>
    private RectInt32 GetWindowedRect()
    {
        var (monitor, work) = GetMonitorRects();
        _ = monitor;

        var width = Math.Min(WindowedWidth, work.Width);
        var height = Math.Min(WindowedHeight, work.Height);

        return new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height);
    }

    /// <summary>查询所在显示器的完整矩形与工作区矩形；查询失败兜底回主显示器。</summary>
    /// <returns>显示器矩形与工作区矩形（物理像素）。</returns>
    private (RectInt32 Monitor, RectInt32 Work) GetMonitorRects()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var monitorHandle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);

        var info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(info);

        if (GetMonitorInfo(monitorHandle, ref info))
        {
            return (
                new RectInt32(info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top),
                new RectInt32(info.rcWork.Left, info.rcWork.Top,
                    info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top));
        }

        // 查询失败兜底：退回主显示器，保证窗口始终铺满一块屏幕。
        var primary = DisplayArea.Primary;
        return (primary.WorkArea, primary.WorkArea);
    }

    /// <summary>清理残留的标题栏与粗边框样式位并强制重算框架，消除隐形 resize 边距实现真贴边。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    private static void ClearFrameStyles(nint hwnd) => SetFrameStyles(hwnd, add: 0, remove: WsCaption | WsThickFrame);

    /// <summary>增删窗口的标题栏 / 粗边框样式位并强制重算非客户区框架。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="add">要置位的样式位。</param>
    /// <param name="remove">要清除的样式位。</param>
    private static void SetFrameStyles(nint hwnd, long add, long remove)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, (nint)((style & ~remove) | add));
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

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(nint hwnd);

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
