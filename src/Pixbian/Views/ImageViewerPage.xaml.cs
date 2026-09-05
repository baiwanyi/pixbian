/**
 * 图片查看器页代码后置（M3）。
 * 职责：设置数据上下文、处理幻灯片开关与键盘快捷键，按设置播放图片转场动画，
 *      并在离开页面时停止播放。
 * 复用约定：视图模型由依赖注入提供；页面本身不持有图片数据，全部通过绑定获取。
 * 关键约束：幻灯片切换按钮是 AppBarToggleButton，其 Checked/Unchecked 事件与 ViewModel 命令
 *          只能选其一驱动状态，否则会出现状态回环；此处统一由事件调用命令，控件只做单向展示；
 *          转场请求发自图片解码后的线程，必须经 DispatcherQueue 切回 UI 线程才能操作元素；
 *          动画每轮重建并复位起始值——FillBehavior 默认 HoldEnd，上一轮压到 0 的 Opacity
 *          不会自行恢复，复用实例会让下一轮的旧图一进场就全透明；
 *          动画目标直接取元素对象而非 TargetName 路径，绕开 namescope 解析的失败可能。
 */

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Pixbian.Core.Models;
using Pixbian.ViewModels;
using Windows.System;

namespace Pixbian.Views;

/// <summary>图片查看器页。</summary>
public sealed partial class ImageViewerPage : Page
{
    /// <summary>转场动画时长。</summary>
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(280));

    /// <summary>滑动模式下新旧图的横向位移量（逻辑像素）。</summary>
    private const double SlideOffset = 60;

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
    }

    /// <summary>查看器视图模型。</summary>
    public ImageViewerViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 获得焦点后键盘快捷键（左右翻页、空格播放）才会命中本页面。
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面必须停止定时器，否则后台会持续触发切换。
        ViewModel.StopSlideShow();
    }

    private void OnSlideShowChecked(object sender, RoutedEventArgs e) => ViewModel.StartSlideShow();

    private void OnSlideShowUnchecked(object sender, RoutedEventArgs e) => ViewModel.StopSlideShow();

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => ViewModel.RotateRight();

    private void OnCloseClick(object sender, RoutedEventArgs e) => App.Services
        .GetRequiredService<MainWindow>()
        .CloseViewer();

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

        storyboard.Children.Add(CreateDoubleAnimation(PreviousImageElement, "Opacity", 1, 0));
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageElement, "Opacity", 0, 1));

        return storyboard;
    }

    /// <summary>水平滑动：旧图向左退出、新图自右进入，两者同时淡变以免边缘出现硬切。</summary>
    private Storyboard CreateSlideStoryboard()
    {
        var storyboard = new Storyboard { Duration = TransitionDuration };

        storyboard.Children.Add(CreateDoubleAnimation(PreviousImageTransform, "X", 0, -SlideOffset));
        storyboard.Children.Add(CreateDoubleAnimation(PreviousImageElement, "Opacity", 1, 0));
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageTransform, "X", SlideOffset, 0));
        storyboard.Children.Add(CreateDoubleAnimation(DisplayImageElement, "Opacity", 0, 1));

        return storyboard;
    }

    /// <summary>构造一条已绑定目标与属性的双精度动画。</summary>
    /// <param name="target">动画目标（元素或变换对象）。</param>
    /// <param name="propertyPath">目标属性名；对变换对象直接写属性名，无需完整路径。</param>
    /// <param name="from">起始值。</param>
    /// <param name="to">结束值。</param>
    private static DoubleAnimation CreateDoubleAnimation(
        DependencyObject target,
        string propertyPath,
        double from,
        double to)
    {
        var animation = new DoubleAnimation
        {
            Duration = TransitionDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            From = from,
            To = to
        };

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
                e.Handled = true;
                break;
        }
    }
}
