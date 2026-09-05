/**
 * 图片查看器代码后置（Lightbox 叠加层）。
 * 职责：设置数据上下文与焦点、处理键盘快捷键，播放首图入场动画与关闭淡出动画，
 *      承担滚轮缩放 / 翻页（按设置；Ctrl + 滚轮始终缩放）、按住拖动平移、
 *      双击缩放、按缩放首选项应用打开时缩放，以及底部工具栏显隐调度。
 * 复用约定：视图模型由依赖注入提供，页面不持有图片数据，全部通过绑定获取；
 *          页面由独立查看器窗口（ImageViewerWindow）承载，关闭统一经 Owner.Close() 收口，
 *          Closed 摘除内容后 Unloaded 负责停表等清理。
 * 关键约束：动画目标直接取元素对象而非 TargetName（namescope 解析失败即静默无动画），
 *          入场动画每轮前必须复位起始值（FillBehavior 默认 HoldEnd 会保留上一轮终值）；
 *          TransformGroup 声明顺序必须为 Translate→Rotate→Scale，平移才是屏幕空间语义，
 *          缩放锚点与拖动平移的坐标公式均依赖该顺序；
 *          平移必须在每次缩放/旋转后经 ClampPan 钳制，防止图像被拖出视口；
 *          幻灯片开关状态由事件驱动、控件单向展示，避免事件与命令互相回写；
 *          工具栏淡出计时在指针悬停于工具栏上时必须暂停，否则无法点击栏内按钮。
 */

using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.ViewModels;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Pixbian.Views;

/// <summary>图片查看器页。</summary>
public sealed partial class ImageViewerPage : Page
{
    /// <summary>首图入场动画时长。</summary>
    private static readonly Duration EntryDuration = new(TimeSpan.FromMilliseconds(250));

    /// <summary>关闭时内容淡出时长。</summary>
    private static readonly Duration CloseFadeDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>工具栏淡入淡出时长。</summary>
    private static readonly Duration ToolbarFadeDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>转场动画时长。</summary>
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(280));

    /// <summary>滚轮翻页节流：触摸板平滑滚动会连发大量小 delta，窗口期内忽略，250ms 至多翻一张。</summary>
    private static readonly TimeSpan WheelNavigateThrottle = TimeSpan.FromMilliseconds(250);

    /// <summary>上次滚轮翻页时刻（UTC），与 WheelNavigateThrottle 配合实现节流。</summary>
    private DateTimeOffset _lastWheelNavigateUtc;

    /// <summary>「按实际大小」是否在等视口布局就绪后重试（SizeChanged 只挂一次）。</summary>
    private bool _initialZoomPendingLayout;

    /// <summary>滑动模式下新旧图的横向位移量（逻辑像素）。</summary>
    private const double SlideOffset = 60;

    /// <summary>首图入场的起始缩放比。</summary>
    private const double EntryStartScale = 0.9;

    /// <summary>点击与拖动的位移判定阈值（逻辑像素）。</summary>
    private const double DragThreshold = 4;

    /// <summary>工具栏无操作自动淡出的时长（毫秒）。</summary>
    private const int ToolbarAutoHideMilliseconds = 3000;

    private readonly DispatcherQueueTimer _toolbarHideTimer;

    private bool _isToolbarVisible;
    private bool _hasEntryAnimationPlayed;
    private bool _isPointerPressed;
    private bool _isDragging;
    private bool _isClosing;
    private Point _pressPoint;

    /// <summary>关闭淡出动画实例；HoldEnd 会把根层 Opacity 钉在 0，下次打开前必须 Stop。</summary>
    private Storyboard? _closeStoryboard;

    /// <summary>初始化图片查看器页。</summary>
    /// <param name="viewModel">查看器视图模型，由依赖注入提供。</param>
    public ImageViewerPage(ImageViewerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnKeyDown;

        // 本页与视图模型同为 DI 单例、生命周期一致，故不在 Unloaded 里退订；
        // 若日后续任一方改为瞬态，必须在此配对退订，否则页面实例会被事件长期持有。
        ViewModel.TransitionRequested += OnTransitionRequested;
        ViewModel.PropertyChanged += OnViewerPropertyChanged;

        _toolbarHideTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toolbarHideTimer.Interval = TimeSpan.FromMilliseconds(ToolbarAutoHideMilliseconds);
        _toolbarHideTimer.IsRepeating = false;
        _toolbarHideTimer.Tick += (_, _) => HideToolbar();
    }

    /// <summary>查看器视图模型。</summary>
    public ImageViewerViewModel ViewModel { get; }

    /// <summary>承载本页的查看器窗口；关闭查看器统一经它收口（Close 触发本页 Unloaded 清理）。</summary>
    public ImageViewerWindow? Owner { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 获得焦点后键盘快捷键（左右翻页、空格播放）才会命中本页面。
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面必须停止定时器，否则后台会持续触发切换。
        ViewModel.StopSlideShow();
        _toolbarHideTimer.Stop();
    }

    private void OnSlideShowChecked(object sender, RoutedEventArgs e) => ViewModel.StartSlideShow();

    private void OnSlideShowUnchecked(object sender, RoutedEventArgs e) => ViewModel.StopSlideShow();

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => ViewModel.RotateRight();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithFade();

    /// <summary>
    /// 关闭查看器：图片从 100% 缩回 90% 并整体渐隐（入场动画的逆过程），
    /// 完成后关闭窗口；关闭动画进行中忽略重复触发。
    /// </summary>
    private void CloseWithFade()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _toolbarHideTimer.Stop();

        // 不设 From：从当前透明度/缩放开始。HoldEnd 会在播完后把根层 Opacity 钉在 0、
        // 缩放钉在 0.9——页面为跨窗口复用的单例，该 storyboard 实例必须存字段，
        // 供下次 BeginOpen 时 Stop 以清除残留。
        var storyboard = new Storyboard { Duration = CloseFadeDuration };
        storyboard.Children.Add(CreateDoubleAnimation(RootLayer, "Opacity", null, 0, CloseFadeDuration));
        storyboard.Children.Add(CreateDoubleAnimation(EntryScaleTransform, "ScaleX", null, EntryStartScale, CloseFadeDuration));
        storyboard.Children.Add(CreateDoubleAnimation(EntryScaleTransform, "ScaleY", null, EntryStartScale, CloseFadeDuration));

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            Owner?.Close();
        }

        storyboard.Completed += OnCompleted;
        _closeStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>打开查看器：复位入场状态，等待首图就绪播放入场动画；与首图解码并行。</summary>
    public void BeginOpen()
    {
        _hasEntryAnimationPlayed = false;

        // 页面是跨窗口复用的单例：上一次关闭淡出的 HoldEnd（根层 Opacity=0）与关闭中
        // 标志都会残留，不复位则新窗口整层透明（只显示窗口不显示图片）且 Esc 无法关闭。
        // HoldEnd 生效时本地赋值无效，必须先 Stop 旧 storyboard 清除其钉住效果。
        _closeStoryboard?.Stop();
        _closeStoryboard = null;
        _isClosing = false;
        RootLayer.Opacity = 1;

        // 工具栏恢复默认隐藏态；平移与入场变换复位（HoldEnd 会保留上一轮终值）。
        _isToolbarVisible = false;
        ToolbarRoot.Opacity = 0;
        ToolbarRoot.IsHitTestVisible = false;
        _toolbarHideTimer.Stop();
        PanTransform.X = 0;
        PanTransform.Y = 0;
        DisplayImageElement.Opacity = 0;
        EntryScaleTransform.ScaleX = EntryStartScale;
        EntryScaleTransform.ScaleY = EntryStartScale;
        UpdateFailedHint();

        // 新窗口激活后焦点可能落在别处，重聚本页保证键盘快捷键可用。
        Focus(FocusState.Programmatic);
    }

    /// <summary>首图入场：图像淡入并从 90% 放大到 100%，每次打开只播一次。</summary>
    private void PlayEntryAnimation()
    {
        _hasEntryAnimationPlayed = true;

        var storyboard = new Storyboard { Duration = EntryDuration };
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageElement, "Opacity", 0, 1, EntryDuration));
        storyboard.Children.Add(CreateDoubleAnimation(EntryScaleTransform, "ScaleX", EntryStartScale, 1.0, EntryDuration));
        storyboard.Children.Add(CreateDoubleAnimation(EntryScaleTransform, "ScaleY", EntryStartScale, 1.0, EntryDuration));
        storyboard.Begin();
    }

    /// <summary>视图模型属性变化：缩放/旋转后钳制平移；首图就绪播入场动画或更新失败提示。</summary>
    private void OnViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.Zoom):
            case nameof(ViewModel.RotationDegrees):
                ClampPan();
                break;

            case nameof(ViewModel.DisplayImage):
                if (!_hasEntryAnimationPlayed)
                {
                    if (ViewModel.DisplayImage is not null)
                    {
                        PlayEntryAnimation();
                    }
                }
                else
                {
                    UpdateFailedHint();
                }

                break;

            case nameof(ViewModel.SourceImage):
                ApplyInitialZoomIfNeeded();
                break;

            case nameof(ViewModel.CurrentItem):
                if (ViewModel.ViewerInitialZoom == ViewerInitialZoom.ActualSize)
                {
                    // 切图瞬间用元数据宽高预应用实际大小（全图尚未解码），消除「先适应窗口再放大」的跳变。
                    var approxZoom = GetActualSizeZoomFromMetadata();

                    if (approxZoom is not null)
                    {
                        ViewModel.SetZoom(approxZoom.Value);
                    }
                }

                break;

            case nameof(ViewModel.ViewerInitialZoom):
                ApplyInitialZoomForSettingChange();
                break;
        }
    }

    /// <summary>失败提示仅在首图已就绪过、且当前无任何可显示图像时出现（加载期间静默）。</summary>
    private void UpdateFailedHint() =>
        FailedHint.Visibility = !_hasEntryAnimationPlayed || ViewModel.DisplayImage is not null
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>显示工具栏并重置自动淡出计时。</summary>
    private void ShowToolbar()
    {
        _isToolbarVisible = true;
        ToolbarRoot.IsHitTestVisible = true;

        var storyboard = new Storyboard { Duration = ToolbarFadeDuration };
        storyboard.Children.Add(CreateDoubleAnimation(ToolbarRoot, "Opacity", null, 1, ToolbarFadeDuration));
        storyboard.Begin();

        RestartToolbarHideTimer();
    }

    /// <summary>隐藏工具栏。</summary>
    private void HideToolbar()
    {
        if (!_isToolbarVisible)
        {
            return;
        }

        _isToolbarVisible = false;
        _toolbarHideTimer.Stop();
        ToolbarRoot.IsHitTestVisible = false;

        var storyboard = new Storyboard { Duration = ToolbarFadeDuration };
        storyboard.Children.Add(CreateDoubleAnimation(ToolbarRoot, "Opacity", null, 0, ToolbarFadeDuration));
        storyboard.Begin();
    }

    private void ToggleToolbar()
    {
        if (_isToolbarVisible)
        {
            HideToolbar();
        }
        else
        {
            ShowToolbar();
        }
    }

    private void RestartToolbarHideTimer()
    {
        _toolbarHideTimer.Stop();
        _toolbarHideTimer.Start();
    }

    private void OnToolbarPointerEntered(object sender, PointerRoutedEventArgs e) => _toolbarHideTimer.Stop();

    private void OnToolbarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isToolbarVisible)
        {
            RestartToolbarHideTimer();
        }
    }

    private void OnImageHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageHost);

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPointerPressed = true;
        _isDragging = false;
        _pressPoint = point.Position;
        ImageHost.CapturePointer(e.Pointer);
    }

    private void OnImageHostPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        var point = e.GetCurrentPoint(ImageHost).Position;

        if (!_isDragging)
        {
            if (Distance(point, _pressPoint) < DragThreshold)
            {
                return;
            }

            // 未放大时图像不溢出视口，无平移余地，拖动无意义。
            if (ViewModel.Zoom <= 1.001)
            {
                return;
            }

            _isDragging = true;
        }

        PanTransform.X += point.X - _pressPoint.X;
        PanTransform.Y += point.Y - _pressPoint.Y;
        _pressPoint = point;
        ClampPan();
        e.Handled = true;
    }

    private void OnImageHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        _isPointerPressed = false;
        ImageHost.ReleasePointerCapture(e.Pointer);

        if (_isDragging)
        {
            _isDragging = false;
            return;
        }

        // 位移超阈值视为滑动而非点击，不触发显隐。
        var point = e.GetCurrentPoint(ImageHost).Position;

        if (Distance(point, _pressPoint) >= DragThreshold)
        {
            return;
        }

        // 点击任意处（图像或空白）统一呼出工具栏；关闭只走 Esc 与右上角关闭按钮，防误触。
        ToggleToolbar();
    }

    private void OnImageHostPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPointerPressed = false;
        _isDragging = false;
    }

    /// <summary>滚轮：按设置在缩放与翻页之间切换；Ctrl + 滚轮始终缩放，与设置无关。</summary>
    /// <remarks>滚轮不唤出工具栏：缩放与翻页都是连续操作，工具栏反复弹出会遮挡图像。</remarks>
    private void OnImageHostPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageHost);
        var delta = point.Properties.MouseWheelDelta;

        if (delta != 0)
        {
            if (ViewModel.ViewerWheelMode == ViewerWheelMode.Navigate && !IsCtrlPressed())
            {
                HandleWheelNavigation(delta);
            }
            else
            {
                ZoomAtCursor(point, delta);
            }
        }

        e.Handled = true;
    }

    /// <summary>Ctrl 是否按下。</summary>
    private static bool IsCtrlPressed() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>滚轮翻页：向下滚下一张、向上滚上一张。</summary>
    /// <remarks>
    /// 节流：触摸板平滑滚动会连发大量小 delta，250ms 内只接受一次，避免一次滚动连翻多张。
    /// </remarks>
    private void HandleWheelNavigation(double delta)
    {
        var now = DateTimeOffset.UtcNow;

        if (now - _lastWheelNavigateUtc < WheelNavigateThrottle)
        {
            return;
        }

        _lastWheelNavigateUtc = now;

        _ = delta < 0
            ? ViewModel.GoNextCommand.ExecuteAsync(null)
            : ViewModel.GoPreviousCommand.ExecuteAsync(null);
    }

    /// <summary>滚轮缩放：以光标为锚点，保持光标下的图像点缩放前后位置不变。</summary>
    private void ZoomAtCursor(PointerPoint point, double delta)
    {
        var oldZoom = ViewModel.Zoom;
        var factor = delta > 0 ? ImageViewerViewModel.ZoomStep : 1 / ImageViewerViewModel.ZoomStep;
        ViewModel.SetZoom(oldZoom * factor);

        var newZoom = ViewModel.Zoom;

        if (Math.Abs(newZoom - oldZoom) > 0.0001)
        {
            // T' = cursor − (zoom'/zoom)·(cursor − T)：光标锚点公式（平移为屏幕空间语义）。
            var k = newZoom / oldZoom;
            var cursor = point.Position;
            PanTransform.X = cursor.X - ((cursor.X - PanTransform.X) * k);
            PanTransform.Y = cursor.Y - ((cursor.Y - PanTransform.Y) * k);
            ClampPan();
        }
    }

    /// <summary>双击在适应窗口与 100% 实际像素之间切换，以双击点为锚。</summary>
    private void OnImageHostDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var contentRect = GetImageContentRect();

        if (contentRect.IsEmpty)
        {
            return;
        }

        double targetZoom;

        if (ViewModel.Zoom > 1.001)
        {
            targetZoom = 1.0;
        }
        else
        {
            var actualSizeZoom = GetActualSizeZoom();

            if (actualSizeZoom is null)
            {
                return;
            }

            // 100% 实际像素：内容显示宽度与位图像素宽之比即目标缩放比。
            targetZoom = actualSizeZoom.Value;
        }

        var oldZoom = ViewModel.Zoom;
        ViewModel.SetZoom(targetZoom);
        var newZoom = ViewModel.Zoom;

        if (Math.Abs(newZoom - oldZoom) > 0.0001)
        {
            var k = newZoom / oldZoom;
            var anchor = e.GetPosition(ImageHost);
            PanTransform.X = anchor.X - ((anchor.X - PanTransform.X) * k);
            PanTransform.Y = anchor.Y - ((anchor.Y - PanTransform.Y) * k);
        }

        ClampPan();
        e.Handled = true;
    }

    /// <summary>缩放首选项变更即时生效：适应窗口立即复位当前图；实际大小立即应用到当前图。</summary>
    private void ApplyInitialZoomForSettingChange()
    {
        if (ViewModel.ViewerInitialZoom == ViewerInitialZoom.FitToWindow)
        {
            PanTransform.X = 0;
            PanTransform.Y = 0;
            ViewModel.SetZoom(1.0);
            ClampPan();
            return;
        }

        // 切到实际大小：全图已就绪用精确值，否则退回元数据宽高近似（SourceImage 就绪时会被校正）。
        var targetZoom = GetActualSizeZoom() ?? GetActualSizeZoomFromMetadata();

        if (targetZoom is null)
        {
            return;
        }

        ViewModel.SetZoom(targetZoom.Value);
        ClampPan();
    }

    /// <summary>用索引库元数据宽高估算 100% 缩放比；宽高未回填或视口未就绪返回 null。</summary>
    /// <remarks>口径与 GetImageContentRect 一致（Uniform 适配），未计 EXIF 显示旋转，与双击 100% 同口径。</remarks>
    private double? GetActualSizeZoomFromMetadata()
    {
        var item = ViewModel.CurrentItem;

        if (item?.Width is not int pixelWidth || pixelWidth <= 0
            || item.Height is not int pixelHeight || pixelHeight <= 0
            || ImageHost.ActualWidth <= 0
            || ImageHost.ActualHeight <= 0)
        {
            return null;
        }

        double imageAspect = (double)pixelWidth / pixelHeight;
        double viewAspect = ImageHost.ActualWidth / ImageHost.ActualHeight;
        double contentWidth = imageAspect > viewAspect
            ? ImageHost.ActualWidth
            : ImageHost.ActualHeight * imageAspect;

        return pixelWidth / contentWidth;
    }

    /// <summary>100% 实际像素对应的缩放比（位图像素宽与内容显示宽之比）；图像未就绪返回 null。</summary>
    /// <remarks>与双击的 100% 同一口径，均未计显示旋转：横向旋转后 100% 以原宽为准，可接受。</remarks>
    private double? GetActualSizeZoom()
    {
        var contentRect = GetImageContentRect();

        if (contentRect.IsEmpty
            || ViewModel.DisplayImage is not BitmapImage bitmap
            || bitmap.PixelWidth == 0)
        {
            return null;
        }

        return bitmap.PixelWidth / contentRect.Width;
    }

    /// <summary>按缩放首选项应用打开时缩放：适应窗口已在装载时复位，此处只处理「按实际大小」。</summary>
    /// <remarks>
    /// 只在全分辨率图就绪（SourceImage 赋值）时应用——低清预览先到，按它的像素宽换算 100% 会错；
    /// 平移先归零再钳制，实际大小从居中状态起步，与适应窗口的复位语义一致。
    /// </remarks>
    private void ApplyInitialZoomIfNeeded()
    {
        if (ViewModel.ViewerInitialZoom != ViewerInitialZoom.ActualSize
            || ViewModel.SourceImage is null)
        {
            return;
        }

        var targetZoom = GetActualSizeZoom();

        if (targetZoom is null)
        {
            // 视口尚未完成首次布局（ActualWidth 为 0，缓存命中时图片可能快于布局就绪）：
            // 挂一次 SizeChanged 等布局就绪后重试，避免初始缩放静默丢失。
            if (!_initialZoomPendingLayout)
            {
                _initialZoomPendingLayout = true;
                ImageHost.SizeChanged += OnImageHostSizeChangedForInitialZoom;
            }

            return;
        }

        PanTransform.X = 0;
        PanTransform.Y = 0;
        ViewModel.SetZoom(targetZoom.Value);
        ClampPan();
    }

    /// <summary>布局就绪后的初始缩放重试入口：一次性退订，防止窗口缩放时反复触发。</summary>
    private void OnImageHostSizeChangedForInitialZoom(object sender, SizeChangedEventArgs e)
    {
        ImageHost.SizeChanged -= OnImageHostSizeChangedForInitialZoom;
        _initialZoomPendingLayout = false;
        ApplyInitialZoomIfNeeded();
    }

    /// <summary>图像内容在视口中的实际显示矩形（Uniform 适配后的区域，未含 RenderTransform）。</summary>
    private Rect GetImageContentRect()
    {
        if (ViewModel.DisplayImage is not BitmapImage bitmap
            || bitmap.PixelWidth == 0
            || bitmap.PixelHeight == 0
            || ImageHost.ActualWidth <= 0
            || ImageHost.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        double viewWidth = ImageHost.ActualWidth;
        double viewHeight = ImageHost.ActualHeight;
        double imageAspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        double viewAspect = viewWidth / viewHeight;

        double contentWidth = imageAspect > viewAspect ? viewWidth : viewHeight * imageAspect;
        double contentHeight = imageAspect > viewAspect ? viewWidth / imageAspect : viewHeight;

        return new Rect(
            (viewWidth - contentWidth) / 2,
            (viewHeight - contentHeight) / 2,
            contentWidth,
            contentHeight);
    }

    /// <summary>
    /// 钳制平移：内容超出视口时限制在视口范围内，未超出时保持居中，防止图像被拖出视野。
    /// </summary>
    private void ClampPan()
    {
        var contentRect = GetImageContentRect();

        if (contentRect.IsEmpty)
        {
            return;
        }

        // 临时清零平移求出「不含平移」的内容包围盒（base），随即恢复——
        // 属性读写同步完成，中间态不会进入渲染帧。
        double savedX = PanTransform.X;
        double savedY = PanTransform.Y;
        PanTransform.X = 0;
        PanTransform.Y = 0;
        var baseBounds = DisplayImageElement.TransformToVisual(ImageHost).TransformBounds(contentRect);
        PanTransform.X = savedX;
        PanTransform.Y = savedY;

        PanTransform.X = ClampPanAxis(savedX, baseBounds.X, baseBounds.Width, ImageHost.ActualWidth);
        PanTransform.Y = ClampPanAxis(savedY, baseBounds.Y, baseBounds.Height, ImageHost.ActualHeight);
    }

    /// <summary>按轴钳制平移量：内容不超出视口时归零（居中），超出时限制在视口范围内。</summary>
    private static double ClampPanAxis(double current, double baseOffset, double size, double view)
    {
        if (size <= view)
        {
            return 0;
        }

        var min = view - size - baseOffset;
        var max = -baseOffset;
        return Math.Clamp(current, min, max);
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>新图可显示时由视图模型发起转场请求。</summary>
    private void OnTransitionRequested(object? sender, EventArgs e)
    {
        // 请求发自解码 await 之后，彼时可能已在线程池线程，动画必须由 UI 线程播放。
        DispatcherQueue.TryEnqueue(BeginTransition);
    }

    /// <summary>按当前设置播放一次转场动画。</summary>
    private void BeginTransition()
    {
        var storyboard = ViewModel.SlideShowTransition == SlideShowTransitionMode.Fade
            ? CreateFadeStoryboard()
            : CreateSlideStoryboard();

        // 复位起始值：HoldEnd 会保留上一轮的终值，不复位则旧图一进场就是透明的。
        PreviousImageElement.Opacity = 1;
        DisplayImageElement.Opacity = 1;
        PreviousImageTransform.X = 0;
        DisplayImageTransform.X = 0;

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            ViewModel.CompleteTransition();
        }

        storyboard.Completed += OnCompleted;
        storyboard.Begin();
    }

    /// <summary>交叉淡入淡出：旧图淡出、新图淡入。</summary>
    private Storyboard CreateFadeStoryboard()
    {
        var storyboard = new Storyboard { Duration = TransitionDuration };

        storyboard.Children.Add(CreateDoubleAnimation(PreviousImageElement, "Opacity", 1, 0, TransitionDuration));
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageElement, "Opacity", 0, 1, TransitionDuration));

        return storyboard;
    }

    /// <summary>水平滑动：旧图向左退出、新图自右进入，两者同时淡变以免边缘出现硬切。</summary>
    private Storyboard CreateSlideStoryboard()
    {
        var storyboard = new Storyboard { Duration = TransitionDuration };

        storyboard.Children.Add(CreateDoubleAnimation(PreviousImageTransform, "X", 0, -SlideOffset, TransitionDuration));
        storyboard.Children.Add(CreateDoubleAnimation(PreviousImageElement, "Opacity", 1, 0, TransitionDuration));
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageTransform, "X", SlideOffset, 0, TransitionDuration));
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageElement, "Opacity", 0, 1, TransitionDuration));

        return storyboard;
    }

    /// <summary>构造一条已绑定目标与属性的双精度动画；from 为 null 时从当前值开始。</summary>
    /// <param name="target">动画目标（元素或变换对象）。</param>
    /// <param name="propertyPath">目标属性名；对变换对象直接写属性名，无需完整路径。</param>
    /// <param name="from">起始值；null 表示取目标属性当前值。</param>
    /// <param name="to">结束值。</param>
    /// <param name="duration">动画时长。</param>
    private static DoubleAnimation CreateDoubleAnimation(
        DependencyObject target,
        string propertyPath,
        double? from,
        double to,
        Duration duration)
    {
        var animation = new DoubleAnimation
        {
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            To = to
        };

        if (from.HasValue)
        {
            animation.From = from.Value;
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, propertyPath);

        return animation;
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                await ViewModel.GoPreviousCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case VirtualKey.Right:
            case VirtualKey.Space:
                await ViewModel.GoNextCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                ViewModel.StopSlideShow();
                CloseWithFade();
                e.Handled = true;
                break;
        }
    }
}
