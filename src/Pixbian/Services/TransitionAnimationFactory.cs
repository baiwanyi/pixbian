/**
 * 转场动画工厂。
 * 职责：为媒体查看类页面构造条目切换的转场动画（交叉淡出 / 水平滑动），
 *      旧帧与新帧的目标元素及变换由调用方传入，图片查看器与幻灯片放映共用同一套动效。
 * 复用约定：动画目标直接取元素对象而非 TargetName（namescope 解析失败即静默无动画）；
 *          from 传 null 表示从目标属性当前值开始，供需要保留现场的场景使用。
 * 关键约束：DoubleAnimation 的 FillBehavior 默认 HoldEnd，会保留上一轮终值，
 *          每轮 Begin 前调用方必须复位起始值；滑动模式为旧帧左移退出、新帧自右进入，
 *          位移量由本类常量承担，调用方不得自行定义以免两页动效漂移。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Pixbian.Services;

/// <summary>转场动画工厂：交叉淡出与水平滑动两种条目切换动效。</summary>
public static class TransitionAnimationFactory
{
    /// <summary>滑动模式下新旧帧的横向位移量（逻辑像素）。</summary>
    public const double SlideOffset = 60;

    /// <summary>构造交叉淡出转场：旧帧淡出、新帧淡入。</summary>
    /// <param name="previousElement">旧帧元素。</param>
    /// <param name="displayElement">新帧元素。</param>
    /// <param name="duration">转场时长。</param>
    /// <returns>就绪的 Storyboard，调用方负责挂 Completed 并 Begin。</returns>
    public static Storyboard CreateFadeStoryboard(
        DependencyObject previousElement,
        DependencyObject displayElement,
        Duration duration)
    {
        var storyboard = new Storyboard { Duration = duration };

        storyboard.Children.Add(CreateDoubleAnimation(previousElement, "Opacity", 1, 0, duration));
        storyboard.Children.Add(CreateDoubleAnimation(displayElement, "Opacity", 0, 1, duration));

        return storyboard;
    }

    /// <summary>构造水平滑动转场：旧帧向左退出、新帧自右进入，同时淡变以免边缘硬切。</summary>
    /// <param name="previousElement">旧帧元素。</param>
    /// <param name="previousTransform">旧帧平移变换。</param>
    /// <param name="displayElement">新帧元素。</param>
    /// <param name="displayTransform">新帧平移变换。</param>
    /// <param name="duration">转场时长。</param>
    /// <returns>就绪的 Storyboard，调用方负责挂 Completed 并 Begin。</returns>
    public static Storyboard CreateSlideStoryboard(
        DependencyObject previousElement,
        TranslateTransform previousTransform,
        DependencyObject displayElement,
        TranslateTransform displayTransform,
        Duration duration)
    {
        var storyboard = new Storyboard { Duration = duration };

        storyboard.Children.Add(CreateDoubleAnimation(previousTransform, "X", 0, -SlideOffset, duration));
        storyboard.Children.Add(CreateDoubleAnimation(previousElement, "Opacity", 1, 0, duration));
        storyboard.Children.Add(CreateDoubleAnimation(displayTransform, "X", SlideOffset, 0, duration));
        storyboard.Children.Add(CreateDoubleAnimation(displayElement, "Opacity", 0, 1, duration));

        return storyboard;
    }

    /// <summary>构造一条已绑定目标与属性的双精度动画；from 为 null 时从当前值开始。</summary>
    /// <param name="target">动画目标（元素或变换对象）。</param>
    /// <param name="propertyPath">目标属性名；对变换对象直接写属性名，无需完整路径。</param>
    /// <param name="from">起始值；null 表示取目标属性当前值。</param>
    /// <param name="to">结束值。</param>
    /// <param name="duration">动画时长。</param>
    public static DoubleAnimation CreateDoubleAnimation(
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
}
