/**
 * 列宽（GridLength）补间动画：WinUI 3 未内置 GridLength 的 DoubleAnimation 等价物，
 * 故自定义 DependencyObject 在每帧把数值写回依赖属性，使 Grid 列宽可被平滑过渡。
 * 复用约定：继承自 DependencyObject，依赖属性命名严格遵循 *Property 约定；
 *          仅在主窗口详情面板的列宽同步场景使用，不承载其他状态。
 * 关键约束：动画对象被 Storyboard 缓存复用，且会在不同方向（显示 / 隐藏）间切换目标值，
 *          必须在每帧基于 From / To 计算，不得缓存中间状态，否则方向反转时会跳变。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Pixbian.Animation;

/// <summary>在 Grid 列宽上做数值插值的动画，使列宽变化与面板平移同步。</summary>
public sealed class GridLengthAnimation : DependencyObject
{
    /// <summary>动画目标对象（通常为 ColumnDefinition）。</summary>
    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.Register(nameof(Target), typeof(DependencyObject), typeof(GridLengthAnimation), new PropertyMetadata(null));

    /// <summary>目标列宽依赖属性（通常为 ColumnDefinition.WidthProperty）。</summary>
    public static readonly DependencyProperty TargetPropertyAliasProperty =
        DependencyProperty.Register(nameof(TargetPropertyAlias), typeof(DependencyProperty), typeof(GridLengthAnimation), new PropertyMetadata(null));

    /// <summary>起始列宽（像素值）。</summary>
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation), new PropertyMetadata(0d));

    /// <summary>结束列宽（像素值）。</summary>
    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation), new PropertyMetadata(0d));

    /// <summary>动画时长。</summary>
    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(Duration), typeof(GridLengthAnimation), new PropertyMetadata(default(Duration)));

    /// <summary>缓动函数。</summary>
    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(EasingFunctionBase), typeof(GridLengthAnimation), new PropertyMetadata(null));

    /// <summary>获取或设置动画目标对象。</summary>
    public DependencyObject? Target
    {
        get => (DependencyObject?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>获取或设置目标列宽依赖属性。</summary>
    public DependencyProperty? TargetPropertyAlias
    {
        get => (DependencyProperty?)GetValue(TargetPropertyAliasProperty);
        set => SetValue(TargetPropertyAliasProperty, value);
    }

    /// <summary>获取或设置起始列宽（像素）。</summary>
    public double From
    {
        get => (double)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    /// <summary>获取或设置结束列宽（像素）。</summary>
    public double To
    {
        get => (double)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    /// <summary>获取或设置动画时长。</summary>
    public Duration Duration
    {
        get => (Duration)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>获取或设置缓动函数。</summary>
    public EasingFunctionBase? EasingFunction
    {
        get => (EasingFunctionBase?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    /// <summary>创建并启动本动画，按缓动函数在每帧把 From→To 的像素值写回目标列宽。</summary>
    public void Begin()
    {
        if (Target is null || TargetPropertyAlias is null)
        {
            return;
        }

        var from = From;
        var to = To;
        var duration = Duration.HasTimeSpan ? Duration.TimeSpan : default;
        var easing = EasingFunction;

        if (duration <= TimeSpan.Zero)
        {
            Target.SetValue(TargetPropertyAlias, new GridLength(to, GridUnitType.Pixel));
            return;
        }

        var startTime = DateTime.Now;

        // CompositionTarget.Rendering 在每帧触发，以经过时间比例驱动插值，保证与 Storyboard 动画节奏一致。
        void OnRendering(object? sender, object args)
        {
            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            var t = Math.Min(1d, elapsed / duration.TotalMilliseconds);
            var eased = easing is null ? t : easing.Ease(t);
            var value = from + (to - from) * eased;
            Target.SetValue(TargetPropertyAlias!, new GridLength(value, GridUnitType.Pixel));

            if (t >= 1d)
            {
                CompositionTarget.Rendering -= OnRendering;
            }
        }

        CompositionTarget.Rendering += OnRendering;
    }
}
