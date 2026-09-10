/**
 * 主窗口代码后置——窗口外壳与主题（partial）。
 * 职责：根布局加载后的初始装配（焦点、缩放比、logo）、DPI 变化跟进、标题栏 Passthrough
 *      区域注册、系统标题栏按钮配色，以及设置变更向各视图模型与页面的分发。
 * 复用约定：设置回流统一入口为 ApplySettings（外壳与设置页两条广播链路共用）；
 *          主题映射统一在 App.MapTheme，悬停画刷刷新走 App.ApplyButtonHoverBrushes。
 * 关键约束：Passthrough 矩形为物理像素，须按 RasterizationScale 换算，布局与窗口状态
 *          变化后必须重新注册；系统标题栏按钮配色不随主题自动更新，须显式赋值，
 *          播放态恒取白色系（舞台与顶栏底恒为暗色）。
 */

using System.IO;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Pixbian.Core.Models;

namespace Pixbian.Views;

/// <summary>主窗口的窗口外壳与主题。</summary>
public sealed partial class MainWindow
{
    private double _lastRasterizationScale;

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        // 根布局加载后把焦点移到导航栏，避免搜索框一启动就获得焦点并显示输入光标。
        _ = NavigationViewControl.Focus(FocusState.Programmatic);
        UpdateTitleBarPassthrough();

        // 窗口在不同 DPI 的显示器之间移动时 XamlRoot 会变更缩放比，须持续跟进。
        RootGrid.XamlRoot.Changed += OnXamlRootChanged;
        SyncThumbnailScale();
        UpdateLogoImage();
    }

    /// <summary>按实际生效主题刷新标题栏品牌 logo：浅色模式用深色图、深色模式用浅色图。</summary>
    /// <remarks>
    /// unpackaged 场景 PRI 不索引 Content 项，故按磁盘路径加载（与 app.ico 同一方式）；
    /// 源图已按标题栏显示尺寸预生成为 64px（16 DIP × 400% DPI），故无需限定解码尺寸——
    /// 由 1000px 原图运行时降采样会丢掉 logo 的细笔画。
    /// </remarks>
    private void UpdateLogoImage()
    {
        var fileName = RootGrid.ActualTheme == ElementTheme.Dark ? "logo-dark.png" : "logo-light.png";
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(logoPath))
        {
            return;
        }

        LogoImage.Source = new BitmapImage(new Uri(logoPath));
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SyncThumbnailScale();

    /// <summary>把当前显示缩放比同步给缩略图服务，使缩略图按物理像素解码；缩放比变化时重新加载已显示的缩略图。</summary>
    private void SyncThumbnailScale()
    {
        if (RootGrid.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var scale = xamlRoot.RasterizationScale;

        if (Math.Abs(scale - _lastRasterizationScale) < 0.01)
        {
            return;
        }

        // 首次同步时还没有已加载的缩略图，无需触发重新加载。
        var isFirstSync = _lastRasterizationScale == 0;
        _lastRasterizationScale = scale;
        _thumbnails.RasterizationScale = scale;

        if (!isFirstSync)
        {
            _ = _gallery.RefreshThumbnailsAsync();
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarPassthrough();

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        UpdateTitleBarPassthrough();

        // 切回应用时复查扫描源目录：外接盘拔出、网络盘断开只在此刻才可能被察觉，
        // 探测本身在线程池执行，不阻塞激活路径。
        _ = RefreshFolderAvailabilityAsync();
    }

    /// <summary>
    /// 把标题栏交互控件的矩形注册为指针 Passthrough 区域。
    /// 矩形为物理像素（须按显示缩放换算），布局或窗口状态变化后必须重新注册。
    /// </summary>
    private void UpdateTitleBarPassthrough()
    {
        if (AppWindow is null || RootGrid.XamlRoot is null)
        {
            return;
        }

        var scale = RootGrid.XamlRoot.RasterizationScale;
        var rects = new List<RectInt32>();

        // 播放态下标题栏行由播放器页接管：放行其顶栏内的交互控件（返回按钮）；
        // 常态放行搜索框与设置按钮。被隐藏的一侧 ActualWidth 为 0、会被下方跳过，
        // 故按状态二选一即可，无需合并集合。
        IEnumerable<FrameworkElement> elements = IsViewerVisible
            && VideoHost.Content is VideoPlayerPage videoPage
                ? videoPage.TitleBarInteractiveElements
                : [SearchBox, SettingsButton];

        foreach (var element in elements)
        {
            if (element is not { IsLoaded: true, ActualWidth: > 0 } framework)
            {
                continue;
            }

            var bounds = framework.TransformToVisual(null)
                .TransformBounds(new Rect(default, new Size(framework.ActualWidth, framework.ActualHeight)));

            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        ApplySettings(settings);
    }

    private void ApplySettings(AppSettings settings)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = App.MapTheme(settings.Theme);

            // 系统标题栏按钮不随应用主题变化，须在主题切换后显式刷新一次。
            UpdateCaptionButtonColors();

            // 按钮悬停 / 按下底色随主题切换刷新（主题字典覆盖在浅色下不生效，见 App）。
            App.ApplyButtonHoverBrushes(root.ActualTheme == ElementTheme.Dark);
        }

        // SetThumbnailSizeAsync 的同步段先更新尺寸值，随后的 ApplyThumbnailSize
        // 触发的属性通知才能读到新值，顺序不可颠倒。
        _ = _gallery.SetThumbnailSizeAsync(settings.ThumbnailSize);
        _galleryPage.ApplyViewMode(settings.ViewMode);
        _galleryPage.ApplyThumbnailSize();

        // 幻灯片间隔、播放顺序与切换方式：查看器不反向依赖设置服务，由外壳在设置变更时推送，
        // 放映途中改设置也能即时生效（ApplySettings 内部会保留播放状态续跑定时器）。
        _viewer.ApplySettings(settings);

        // 放映窗口打开期间（含放映内选项 Flyout 写回）同样须把设置推给放映视图模型；
        // 窗口未打开时无接收方，跳过（打开放映前 OpenSlideShowAsync 会先推送一次）。
        _slideShowWindow?.Page.ViewModel.ApplySettings(settings);

        // 片段区间档位：短片页同样不反向依赖设置服务，与幻灯片共用 ClipRangePlanner 规则。
        _shortPage.ViewModel.ApplySettings(settings);

        NotifyTargetChanged();
    }

    /// <summary>跟随系统主题时，系统深浅反转同步刷新标题栏按钮颜色（背景渐变经 ThemeResource 随主题自动切换）。</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtonColors();
        UpdateLogoImage();

        // 按钮悬停 / 按下底色随主题反转同步刷新。
        App.ApplyButtonHoverBrushes(sender.ActualTheme == ElementTheme.Dark);
    }

    /// <summary>按实际生效主题刷新系统标题栏按钮（最小化/最大化/关闭）颜色。</summary>
    /// <remarks>
    /// ExtendsContentIntoTitleBar 开启后，系统按钮前景色不随应用主题更新，必须显式赋值；
    /// 按钮底色一律转透明以融入标题栏行（该行为透明，背景图由根布局底层透出）。
    /// 悬停/按下底色也必须显式赋值：不设时回落到系统默认高亮（暗色下约 20% 白），
    /// 视觉上远重于 Fluent 的 Subtle 反馈；此处取值与 SubtleFillColorSecondary/Tertiary
    /// 两个主题令牌一致（暗 8%/6% 白、浅 6%/4% 黑），与设置按钮的悬停观感对齐。
    /// 跟随系统时 ActualTheme 由系统决定，故此处读实际主题而非设置值。
    /// 播放态恒取白色系：舞台与顶栏底恒为暗色，与主题无关——浅色主题下若按主题取黑，
    /// 按钮会变成黑字压黑底而不可见。
    /// </remarks>
    private void UpdateCaptionButtonColors()
    {
        if (AppWindow?.TitleBar is not { } titleBar)
        {
            return;
        }

        var useLightForeground = IsViewerVisible
            || (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var foreground = useLightForeground ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;

        // 悬停/按下高亮取 Subtle 令牌同值：AppliesToBorder 画刷无法直接用于 AppWindow（要 Color），
        // 故按主题分别给出与令牌等值的 ARGB。
        var hoverBackground = useLightForeground
            ? Microsoft.UI.ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var pressedBackground = useLightForeground
            ? Microsoft.UI.ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(0x0A, 0x00, 0x00, 0x00);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }
}
