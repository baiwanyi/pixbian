/**
 * 转场动画工厂。
 * 职责：为媒体查看类页面构造条目切换的转场动画（水平滑动 / 交叉淡入 / 溶解），
 *      旧帧与新帧的目标元素及变换由调用方传入，图片查看器与幻灯片放映共用同一套动效。
 * 复用约定：动画目标直接取元素对象而非 TargetName（namescope 解析失败即静默无动画）；
 *          from 传 null 表示从目标属性当前值开始，供需要保留现场的场景使用；
 *          缓动默认 CubicEase EaseOut，需要平缓起收的场景显式传入 SineEase EaseInOut。
 * 关键约束：DoubleAnimation 的 FillBehavior 默认 HoldEnd，会保留上一轮终值，
 *          每轮 Begin 前调用方必须复位起始值；
 *          交叉淡入与溶解一律「新帧叠在旧帧之上淡入、旧帧保持不透明托底」——
 *          两层同时半透明会让舞台黑底在中途透出，画面整体发暗一闪，观感即「生硬」；
 *          滑动位移按视口宽取比例由调用方传入（固定 60px 对铺满全屏的图像只有百分之几，
 *          观感是抖动而非滑动）；
 *          溶解的缩放必须作用在调用方独立的「转场缩放」层，与画面扩大动画的缩放层互不共用，
 *          两个 ScaleTransform 相乘，否则后启动的动画会覆盖前一轮的终值。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Pixbian.Services;

/// <summary>转场动画工厂：水平滑动、交叉淡入与溶解三种条目切换动效。</summary>
public static class TransitionAnimationFactory
{
    /// <summary>滑动位移占视口宽度的比例；调用方按视口宽换算后传入。</summary>
    public const double SlideOffsetRatio = 0.08;

    /// <summary>溶解模式下新帧的起始放大倍率：回落到 1.0 形成轻微推进感。</summary>
    public const double DissolveEnterScale = 1.04;

    /// <summary>溶解模式下旧帧的让位放大终值：旧画面轻微放大淡出，像向后让位。</summary>
    public const double DissolvePreviousScale = 1.04;

    /// <summary>构造交叉淡入转场：旧帧淡出与新帧淡入同步进行。</summary>
    /// <remarks>
    /// 旧帧必须全程参与淡出：只淡入新帧而让旧帧托底，会让旧图「一直都在、迟迟不走」，
    /// 观感即拖尾。双层同时半透明时露出的是舞台背景（开启虚化时是模糊背景而非黑底），
    /// 不会发暗——此前「旧帧托底」是为了避免纯黑舞台下的亮度塌陷，已有背景后不再必要。
    /// </remarks>
    /// <param name="previousElement">旧帧元素。</param>
    /// <param name="displayElement">新帧元素（须位于旧帧之上）。</param>
    /// <param name="duration">转场时长。</param>
    /// <returns>就绪的 Storyboard，调用方负责挂 Completed 并 Begin。</returns>
    public static Storyboard CreateFadeStoryboard(
        DependencyObject previousElement,
        DependencyObject displayElement,
        Duration duration)
    {
        var storyboard = new Storyboard { Duration = duration };
        var easing = CreateEaseInOut();

        storyboard.Children.Add(CreateDoubleAnimation(previousElement, "Opacity", 1, 0, duration, easing));
        storyboard.Children.Add(CreateDoubleAnimation(displayElement, "Opacity", 0, 1, duration, easing));

        return storyboard;
    }

    /// <summary>
    /// 构造溶解转场：在交叉淡入基础上让旧帧轻微后退，营造推进层次。
    /// 幻灯片页必须把新帧缩放层传 null——新帧的缩放由画面动画（Ken Burns）承担，
    /// 两者叠加会让合成缩放先减后增，表现为图片边缘抖动。
    /// </summary>
    /// <param name="previousElement">旧帧元素。</param>
    /// <param name="displayElement">新帧元素（须位于旧帧之上）。</param>
    /// <param name="displayScale">新帧的转场缩放层；null 表示不做缩放。</param>
    /// <param name="previousScale">旧帧的转场缩放层；null 表示不做缩放。</param>
    /// <param name="duration">转场时长。</param>
    /// <returns>就绪的 Storyboard，调用方负责挂 Completed 并 Begin。</returns>
    public static Storyboard CreateDissolveStoryboard(
        DependencyObject previousElement,
        DependencyObject displayElement,
        ScaleTransform? displayScale,
        ScaleTransform? previousScale,
        Duration duration)
    {
        var storyboard = new Storyboard { Duration = duration };
        var easing = CreateEaseInOut();

        storyboard.Children.Add(CreateDoubleAnimation(previousElement, "Opacity", 1, 0, duration, easing));
        storyboard.Children.Add(CreateDoubleAnimation(displayElement, "Opacity", 0, 1, duration, easing));

        // 旧帧缩放不写死起点：从其当前缩放（画面动画的移交终态）平滑过渡到让位倍率。
        if (previousScale is not null)
        {
            storyboard.Children.Add(CreateDoubleAnimation(previousScale, "ScaleX", null, DissolvePreviousScale, duration, easing));
            storyboard.Children.Add(CreateDoubleAnimation(previousScale, "ScaleY", null, DissolvePreviousScale, duration, easing));
        }

        if (displayScale is not null)
        {
            storyboard.Children.Add(CreateDoubleAnimation(displayScale, "ScaleX", DissolveEnterScale, 1.0, duration, easing));
            storyboard.Children.Add(CreateDoubleAnimation(displayScale, "ScaleY", DissolveEnterScale, 1.0, duration, easing));
        }

        return storyboard;
    }

    /// <summary>构造水平滑动转场：旧帧向左退出、新帧自右进入，同时淡变以免边缘硬切。</summary>
    /// <param name="previousElement">旧帧元素。</param>
    /// <param name="previousTransform">旧帧平移变换。</param>
    /// <param name="displayElement">新帧元素。</param>
    /// <param name="displayTransform">新帧平移变换。</param>
    /// <param name="offset">横向位移量（逻辑像素），取视口宽乘以 <see cref="SlideOffsetRatio"/>。</param>
    /// <param name="duration">转场时长。</param>
    /// <returns>就绪的 Storyboard，调用方负责挂 Completed 并 Begin。</returns>
    public static Storyboard CreateSlideStoryboard(
        DependencyObject previousElement,
        TranslateTransform previousTransform,
        DependencyObject displayElement,
        TranslateTransform displayTransform,
        double offset,
        Duration duration)
    {
        var storyboard = new Storyboard { Duration = duration };

        // 旧帧平移不写死起点：从其当前平移位置（画面动画的移交终态）继续滑出，
        // 写死 0 会让旧帧在切换瞬间跳回原点。
        storyboard.Children.Add(CreateDoubleAnimation(previousTransform, "X", null, -offset, duration));
        storyboard.Children.Add(CreateDoubleAnimation(previousElement, "Opacity", 1, 0, duration));
        storyboard.Children.Add(CreateDoubleAnimation(displayTransform, "X", offset, 0, duration));
        storyboard.Children.Add(CreateDoubleAnimation(displayElement, "Opacity", 0, 1, duration));

        return storyboard;
    }

    /// <summary>
    /// 构造一条线性推进的双精度动画；匀速持续运动（画面扩大 / 平移）用它，
    /// 缓动会让画面忽快忽慢，反而显得机械。
    /// </summary>
    /// <param name="target">动画目标（元素或变换对象）。</param>
    /// <param name="propertyPath">目标属性名。</param>
    /// <param name="from">起始值；null 表示取目标属性当前值。</param>
    /// <param name="to">结束值。</param>
    /// <param name="duration">动画时长。</param>
    /// <param name="enableDependentAnimation">
    /// 是否允许依赖动画：影响布局的属性（如 ProgressBar.Value）必须置 true，
    /// 否则框架会直接跳过该动画，表现为「值完全不变」且无任何报错。
    /// </param>
    public static DoubleAnimation CreateLinearDoubleAnimation(
        DependencyObject target,
        string propertyPath,
        double? from,
        double to,
        Duration duration,
        bool enableDependentAnimation = false)
    {
        var animation = new DoubleAnimation
        {
            Duration = duration,
            EnableDependentAnimation = enableDependentAnimation,
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

    /// <summary>构造一条已绑定目标与属性的双精度动画；from 为 null 时从当前值开始。</summary>
    /// <param name="target">动画目标（元素或变换对象）。</param>
    /// <param name="propertyPath">目标属性名；对变换对象直接写属性名，无需完整路径。</param>
    /// <param name="from">起始值；null 表示取目标属性当前值。</param>
    /// <param name="to">结束值。</param>
    /// <param name="duration">动画时长。</param>
    /// <param name="easing">缓动函数；null 表示取 CubicEase EaseOut（入场 / 关闭等既有动效的默认值）。</param>
    public static DoubleAnimation CreateDoubleAnimation(
        DependencyObject target,
        string propertyPath,
        double? from,
        double to,
        Duration duration,
        EasingFunctionBase? easing = null)
    {
        var animation = new DoubleAnimation
        {
            Duration = duration,
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
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

    /// <summary>平缓起收的缓动：交叉淡入与溶解用它避免起手过猛、尾段拖沓。</summary>
    private static SineEase CreateEaseInOut() => new() { EasingMode = EasingMode.EaseInOut };
}
