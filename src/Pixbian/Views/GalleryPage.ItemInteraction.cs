/**
 * 图库页代码后置——条目交互（partial）。
 * 职责：等高视图的悬停视觉与元素准备（按需解码触发）、条目单击/双击/收藏按钮手势、
 *      延迟打开查看器的去抖定时、收藏图标弹跳动画与命中解析。
 * 复用约定：条目命中统一经 ResolveHit（Repeater 按索引反查，不用复用期不可靠的 DataContext）；
 *          打开查看器复用 Owner.OpenViewerAsync，收藏切换复用 ViewModel.ToggleFavoriteAsync。
 * 关键约束：悬停判定挂 Repeater 根的冒泡 PointerMoved（模板元素布局期挂接指针事件会
 *          fail-fast）；ElementPrepared 内改绑定属性同样 fail-fast，实际工作必须 TryEnqueue；
 *          悬停与动画只写渲染属性（Opacity / Transform），运行期不得改 Visibility。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>图库页的条目交互。</summary>
public sealed partial class GalleryPage
{
    /// <summary>单击打开查看器的延迟：等待可能的双击收藏手势，双击处理器会取消挂起的打开。</summary>
    private static readonly TimeSpan TapOpenDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>双击按钮的第二次 Click 去抖窗口（毫秒）：双击只切换一次，来回切换等于没变。</summary>
    private const int FavoriteClickDebounceMs = 300;

    /// <summary>双击后的 Tapped 抑制窗口（毫秒）：吞掉双击手势漏出的第二次 Tapped，防止误开查看器。</summary>
    private const int DoubleTapSuppressMs = 400;

    /// <summary>收藏图标弹跳动画的放大峰值。</summary>
    private const double FavoritePopScale = 1.4;

    /// <summary>条目模板内收藏按钮的元素名，供双击时从条目容器定位动画目标。</summary>
    private const string FavoriteButtonName = "FavoriteButton";

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pendingOpenTimer;
    private long _lastFavoriteClickTicks;
    private long _lastDoubleTapTicks;

    /// <summary>模板内复选框与悬停遮罩的名称（供代码后置按名命中）。</summary>
    private const string SelectionCheckName = "SelectionCheck";

    private const string HoverMaskName = "MaskBorder";

    /// <summary>等高条目指针移入：置条目悬停态，驱动复选框浮现与遮罩（模板根非 Control，VSM 失效）。</summary>
    private void OnJustifiedPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 悬停判定挂在 Repeater 根而非模板内：PointerEntered 是直接事件（不冒泡），
        // 只能在模板元素上逐个挂接，而模板元素在布局 pass 内 realize，此时挂接指针事件
        // 会触发 XAML fail-fast（0xc000027b，无托管堆栈）。改用冒泡的 PointerMoved
        // 由命中源上溯到 Repeater 直接子元素，模板保持零事件。
        //
        // 悬停视觉由代码直接写元素而非新增 x:Bind：实测在模板里绑定悬停派生属性
        // （Opacity / 计算属性）会在元素 measure 期间触发 fail-fast（同一崩溃类型），
        // 故一律延到下一个消息、以本地值写入。
        var element = ResolveRepeaterElement(e.OriginalSource as DependencyObject);

        if (element is null || ReferenceEquals(element, _hoveredElement))
        {
            return;
        }

        var previous = _hoveredElement;
        _hoveredElement = element;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (previous is not null)
            {
                ApplyHover(previous, false);
            }

            ApplyHover(element, true);
        });
    }

    /// <summary>指针移出等高视图：清除悬停态。</summary>
    private void OnJustifiedPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var hovered = _hoveredElement;
        _hoveredElement = null;

        if (hovered is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => ApplyHover(hovered, false));
    }

    /// <summary>应用或撤销单个条目的悬停视觉（复选框浮现 + 遮罩）。</summary>
    /// <remarks>
    /// 选择模式下的复选框显隐由模板绑定（条目属性 IsSelectionCheckVisible）负责，
    /// 本方法只在普通模式改写；绑定更新会覆盖本地值，两种驱动不会互相钉死。
    /// </remarks>
    private void ApplyHover(FrameworkElement element, bool hovered)
    {
        // 只写渲染属性（Opacity / IsHitTestVisible）：实测在 Repeater 元素上运行时改
        // Visibility 会触发 XAML fail-fast（0xc000027b），故一律改用不透明度承载显隐。
        if (FindDescendantByName(element, SelectionCheckName) is UIElement check && !IsSelectionMode)
        {
            check.Opacity = hovered ? 1.0 : 0.0;
            check.IsHitTestVisible = hovered;
        }

        if (FindDescendantByName(element, HoverMaskName) is UIElement mask)
        {
            mask.Opacity = hovered ? 0.05 : 0.0;
        }
    }

    /// <summary>从命中源沿可视树上溯到 Repeater 的直接子元素（模板根）。</summary>
    private FrameworkElement? ResolveRepeaterElement(DependencyObject? source)
    {
        var current = source;

        while (current is not null)
        {
            if (current is FrameworkElement element
                && ReferenceEquals(VisualTreeHelper.GetParent(element), JustifiedRepeater))
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>当前悬停的等高条目元素（模板根）。</summary>
    private FrameworkElement? _hoveredElement;

    /// <summary>等高视图条目元素被准备（首次 realize 或回收复用）：估算显示尺寸并触发按需解码。</summary>
    /// <remarks>
    /// ElementPrepared 在 Repeater 的布局 pass 内同步触发——期间改任何绑定属性
    /// （ThumbnailState 等）都会使子元素在布局中失效，触发 XAML fail-fast（0xc000027b
    /// 无托管堆栈），实际工作必须 TryEnqueue 延到下一个消息（与 NavigationView/Expander
    /// 的「回调内同步改状态会出事」同一类教训）。
    /// 显示尺寸先按「行高 × 宽高比」估算回写，布局排列阶段以行表精确值覆盖。
    /// </remarks>
    private void OnJustifiedElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            // 用事件参数 args.Index 而非 GetElementIndex(element)：ForceCreate 时 Repeater
            // 会在 measure pass 内替换元素（新建的临时 element 与最终 element 不是同一个），
            // 旧 element 失去映射、GetElementIndex 返回 -1 触发 early-return，解码请求从未
            // 发出，首格缩略图永远是骨架屏。args.Index 是事件触发时的索引，稳定可信。
            var index = args.Index;

            if (index < 0 || ViewModel.Items.ElementAtOrDefault(index) is not { } item)
            {
                return;
            }

            item.ContainerEverRealized = true;

            // 就地补选择模式的复选框可见性：集合快照式的批量同步覆盖不到后到的条目
            // （分页追加、滚动时新 realize），缺这一步新格子的复选框会停在不可点状态。
            item.IsSelectionCheckVisible = IsSelectionMode;

            // 显式对齐数据上下文：Repeater 设置 DataContext 的时机晚于本事件
            // （prepared 时 DataContext 还是宿主页面），模板 x:Bind 的数据根取自元素
            // DataContext，未对齐会让整份模板绑定静默失效（界面全空但不报错）。
            // 元素可能被 Repeater 在后续 measure 中替换（尤其 ForceCreate 后），但
            // 被替换走的新元素由框架绑到 items[index]，我们写旧 element 是 no-op。
            if (!ReferenceEquals(element.DataContext, item))
            {
                element.DataContext = item;
            }

            var size = ViewModel.ThumbnailSize;
            var estimatedWidth = size * Math.Clamp(item.AspectRatio, 1.0, 2.0);
            item.SetDisplaySize(estimatedWidth, size);

            // 集合替换后补一次视口上报：窗口此前只由滚动驱动，不滚动就永远建立不起来。
            if (!_viewportReported)
            {
                _viewportReported = true;
                DispatcherQueue.TryEnqueue(() => UpdateViewportWindow(JustifiedView));
            }

            // 不 await：宿主的事件须同步返回，等待 IO 会阻塞滚动。
            _ = item.EnsureThumbnailAsync(size);
        });
    }

    /// <summary>命中定位：兼容两条视觉树路径——方形视图的 GridViewItem 容器（取 Content）
    /// 与等高视图的 Repeater 元素（按宿主索引映射反查集合）。返回命中的
    /// 条目与宿主元素（收藏动画等需在容器子树内定位目标时使用）。</summary>
    /// <remarks>等高视图**不得**用元素 DataContext 取条目：ItemsRepeater 无容器机制，元素在
    /// 回收与复用期间 DataContext 可能停留在旧条目，取到的条目与用户所见不符——既会选错图，
    /// 也会把不属于当前集合的游离条目混进选择集合（表现为「点什么都是退出选择模式」）。
    /// Repeater 自己的索引映射与视觉位置严格一致，据此反查集合才是唯一可信来源。</remarks>
    private (MediaItemViewModel? Item, FrameworkElement? Container) ResolveHit(DependencyObject? source)
    {
        if (FindItemContainer(source) is { } container && container.Content is MediaItemViewModel viaGrid)
        {
            return (viaGrid, container);
        }

        if (ResolveRepeaterElement(source) is { } element)
        {
            var index = JustifiedRepeater.GetElementIndex(element);

            if (ViewModel.Items.ElementAtOrDefault(index) is { } byIndex)
            {
                return (byIndex, element);
            }
        }

        // 兜底：元素已失去索引映射（元素正被替换）时退回沿树上溯取 DataContext，
        // 保证右键菜单与收藏手势在无映射时仍可用，宁可用旧上下文也不静默失效。
        for (var node = source as FrameworkElement; node is not null; node = VisualTreeHelper.GetParent(node) as FrameworkElement)
        {
            if (node.DataContext is MediaItemViewModel viaTemplate)
            {
                return (viaTemplate, node);
            }
        }

        return (null, null);
    }

    /// <summary>命中点是否落在复选框内：等高视图选择模式的复选框点击已由 Click 处理，
    /// 冒泡的 Tapped 不得再触发图片主体的选择/打开语义。</summary>
    private static bool IsWithinCheckBox(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is CheckBox)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>单击条目：延迟短暂窗口后在查看器中打开，期间发生双击则被取消；
    /// 勾选式选择模式下单击仍用于切换选中态，不打开。</summary>
    private void OnItemTapped(object sender, TappedRoutedEventArgs e)
    {
        // 阻止事件继续冒泡，避免外层容器（如自适应视图的 ScrollViewer）再次触发本处理程序。
        e.Handled = true;

        // 复选框命中：等高视图的翻转已由 Click 处理，此处不得重复触发。
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var (item, _) = ResolveHit(e.OriginalSource as DependencyObject);

        // 勾选模式：方形视图由 GridView 多选机制处理（此处不作为）；等高视图单击图片主体
        // 切换选中态（照片应用式交互）。
        if (IsSelectionMode)
        {
            if (ReferenceEquals(sender, JustifiedView) && item is not null)
            {
                ViewModel.JustifiedSelection.Toggle(item);
            }

            return;
        }

        // 双击收藏时框架可能漏出第二次 Tapped：落在抑制窗口内的 Tapped 属于双击手势，直接忽略。
        if (Environment.TickCount64 - _lastDoubleTapTicks < DoubleTapSuppressMs)
        {
            return;
        }

        if (item is not null)
        {
            ScheduleOpenViewer(item);
        }
    }

    /// <summary>双击条目：切换收藏并播放图标弹跳动画，挂起的「单击打开查看器」被取消。</summary>
    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        StopPendingOpen();
        _lastDoubleTapTicks = Environment.TickCount64;

        // 选择模式的双击会破坏勾选操作节奏，不承载收藏语义。
        if (IsSelectionMode)
        {
            return;
        }

        var (item, container) = ResolveHit(e.OriginalSource as DependencyObject);

        if (item is null)
        {
            return;
        }

        await ViewModel.ToggleFavoriteAsync(item);

        if (container is not null && FindDescendantByName(container, FavoriteButtonName) is { } host)
        {
            PlayFavoritePopAnimation(host);
        }
    }

    /// <summary>条目收藏按钮单击：与双击图片等效，直接切换收藏并播动画，同时取消挂起的打开。</summary>
    private async void OnFavoriteButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaItemViewModel item })
        {
            return;
        }

        StopPendingOpen();

        // 双击按钮的第二次 Click 去抖：只切换一次，来回切换等于没变。
        var now = Environment.TickCount64;
        if (now - _lastFavoriteClickTicks < FavoriteClickDebounceMs)
        {
            return;
        }
        _lastFavoriteClickTicks = now;

        await ViewModel.ToggleFavoriteAsync(item);
        PlayFavoritePopAnimation((FrameworkElement)sender);
    }

    /// <summary>收藏按钮上的双击就地标记已处理：按钮 Click 已完成切换，不能让事件冒泡到列表再切一次。</summary>
    private void OnFavoriteButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        StopPendingOpen();
    }

    /// <summary>安排延迟打开查看器；等待可能的双击收藏手势，双击处理器会取消本定时器。</summary>
    private void ScheduleOpenViewer(MediaItemViewModel item)
    {
        StopPendingOpen();

        // 页面自身持有当前线程的 DispatcherQueue（DependencyObject.DispatcherQueue），
        // 无需再 GetForCurrentThread；裸类型名会解析到该实例属性而非类型。
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TapOpenDelay;
        timer.IsRepeating = false;
        timer.Tick += async (sender, _) =>
        {
            sender.Stop();
            _pendingOpenTimer = null;
            await Owner.OpenViewerAsync(item);
        };
        timer.Start();
        _pendingOpenTimer = timer;
    }

    /// <summary>取消挂起的「打开查看器」定时器（双击收藏、点击收藏按钮时调用）。</summary>
    private void StopPendingOpen()
    {
        _pendingOpenTimer?.Stop();
        _pendingOpenTimer = null;
    }

    /// <summary>收藏图标弹跳动画：放大到峰值再回落，伴随短暂不透明度增强。</summary>
    /// <remarks>Storyboard 现场创建并以元素对象为目标：Resources 里带 TargetName 的
    /// XAML Storyboard 受 namescope 解析限制，模板实例多且回收重建，须逐实例驱动。
    /// FillBehavior 默认 HoldEnd，每轮开始前必须复位起始值。</remarks>
    private static void PlayFavoritePopAnimation(FrameworkElement host)
    {
        var scale = host.RenderTransform as ScaleTransform;

        if (scale is null)
        {
            scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            host.RenderTransform = scale;
            host.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        scale.ScaleX = 1;
        scale.ScaleY = 1;
        host.Opacity = 1;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var scaleX = new DoubleAnimation { From = 1, To = FavoritePopScale, Duration = duration, AutoReverse = true, EasingFunction = easing };
        var scaleY = new DoubleAnimation { From = 1, To = FavoritePopScale, Duration = duration, AutoReverse = true, EasingFunction = easing };
        var opacity = new DoubleAnimation { From = 0.4, To = 1, Duration = duration };

        var storyboard = new Storyboard();
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Children.Add(opacity);
        Storyboard.SetTarget(scaleX, scale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTarget(scaleY, scale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        Storyboard.SetTarget(opacity, host);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Begin();
    }

    /// <summary>沿视觉树向下按 Name 查找元素，用于从条目容器定位收藏按钮。</summary>
    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is FrameworkElement { Name: var elementName } element && elementName == name)
            {
                return element;
            }

            var descendant = FindDescendantByName(child, name);

            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
