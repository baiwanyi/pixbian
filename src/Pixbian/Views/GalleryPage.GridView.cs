/**
 * 图库页代码后置——方形网格视图（partial）。
 * 职责：ItemsWrapGrid 动态边长计算（防震荡门控）、内部滚动条的视口窗口化与触底翻页、
 *      滚出窗口条目的在途解码取消、条目按压弹簧反馈。
 * 复用约定：视口区间统一经 ViewModel.UpdateViewport 转发调度器；翻页复用 LoadMoreCommand；
 *          面板/滚动条查找复用 FindDescendant。
 * 关键约束：每行个数（perRow）为边长门控——滚动条出现/消失只让宽度小幅变化，perRow 不变时
 *          不写 ItemWidth，阻断「滚动条 ↔ 边长」布局震荡；滚动中间态也更新窗口（调度器
 *          提交受节拍限流），在途取消只作用于「上次窗口减本次窗口」的差集。
 */

using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>图库页的方形网格视图。</summary>
public sealed partial class GalleryPage
{
    /// <summary>网格项间距（相邻格子间隙，由容器模板 Margin 承担）。</summary>
    private const double GridSpacing = 8;

    /// <summary>内容区水平 Padding 总量（左右各 48），格子边长计算时扣除。</summary>
    private const double GridViewHorizontalPadding = 96;

    /// <summary>当前被按压的条目容器；松开 / 取消 / 失去捕获时回弹并清空。</summary>
    private GridViewItem? _pressedItem;

    // 按压反馈参数：下压深度与两段弹簧阻尼（按下临界阻尼干脆；回弹欠阻尼产生一次轻柔回弹）。
    private const float PressedScale = 0.98f;
    private const float PressSpringDamping = 0.9f;
    private const float ReboundSpringDamping = 0.65f;
    private static readonly TimeSpan SpringPeriod = TimeSpan.FromMilliseconds(35);

    private const int GridItemPadding = 8;

    /// <summary>方形视图内建面板与内部滚动条：Loaded 时登记，动态边长与视口窗口化依赖它们。</summary>
    private ItemsWrapGrid? _wrapGrid;
    private ScrollViewer? _gridViewer;

    /// <summary>动态边长计算产物：每行个数（震荡门控基准）与格子边长（视口换算用）。</summary>
    private int _wrapPerRow;
    private double _wrapEdge;

    /// <summary>各滚动视图最近一次窗口化的索引区间：把取消限定在「滚出窗口」的差集上。</summary>
    private readonly Dictionary<ScrollViewer, (int First, int Last)> _lastViewportWindows = [];

    /// <summary>滚动接近底部时加载下一页；两视图共用。</summary>
    private async void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 仅处理自适应视图的外层滚动与方形视图的内部滚动。
        if (sender is not ScrollViewer viewer
            || (viewer != JustifiedView && viewer != _gridViewer))
        {
            return;
        }

        // 拖动/惯性中间态也更新视口窗口：调度器提交受 tick 限流（每拍 ≤4 条）无洪峰风险，
        // 滚动过程中实时收编可让新条目边滚边解，无需等滚动停止（等待感的主要来源）。
        if (e.IsIntermediate)
        {
            UpdateViewportWindow(viewer);
            return;
        }

        // 滚动停止：按视口窗口驱动恢复与取消（O(log n + 窗口)，与总条目数无关）。
        UpdateViewportWindow(viewer);

        // 距底部两屏内即预取，避免用户滚到底后看到空白。
        var remaining = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;
        if (remaining > viewer.ViewportHeight * 2)
        {
            return;
        }

        await ViewModel.LoadMoreCommand.ExecuteAsync(null);
    }

    /// <summary>按视口窗口驱动在途取消，并把可见区间转发调度器收编待解条目。</summary>
    /// <remarks>
    /// 恢复与渐进提交由调度器完成（窗口内无位图条目自动收编，含被缓存淘汰的条目）；
    /// 页面只负责取消：上次窗口减本次窗口的差集条目取消在途解码，把信号量槽位
    /// 让给新进入窗口的条目。差集条目可能并无在途请求（已成功/已置空），
    /// CancelPendingLoad 对两者均无操作，无需前置判断。
    /// </remarks>
    private void UpdateViewportWindow(ScrollViewer viewer)
    {
        var (first, last) = ResolveVisibleIndexRange(viewer);

        if (first < 0)
        {
            _lastViewportWindows.Remove(viewer);
            return;
        }

        var span = last - first + 1;
        var winFirst = Math.Max(0, first - span);
        var winLast = Math.Min(ViewModel.ItemCount - 1, last + span);

        if (_lastViewportWindows.TryGetValue(viewer, out var previous))
        {
            CancelScrolledOutThumbnails(previous, winFirst, winLast);
        }

        _lastViewportWindows[viewer] = (winFirst, winLast);

        // 传未扩展的可见区间：窗口扩展由调度器统一执行。
        ViewModel.UpdateViewport(first, last);
    }

    /// <summary>经面板/布局求当前视口覆盖的数据索引区间；未就绪或列表为空返回 (-1, -1)。</summary>
    private (int First, int Last) ResolveVisibleIndexRange(ScrollViewer viewer)
    {
        if (ViewModel.ItemCount == 0)
        {
            return (-1, -1);
        }

        var top = viewer.VerticalOffset;
        var bottom = top + viewer.ViewportHeight;

        if (viewer == _gridViewer)
        {
            return ResolveWrapGridIndexRange(top, bottom);
        }

        // 等高视图：虚拟化布局的行几何表。
        return JustifiedLayoutCore?.IndexRangeFromY(top, bottom) ?? (-1, -1);
    }

    /// <summary>方形视图按均匀行高直除求覆盖索引区间；行参数由动态边长计算维护。</summary>
    private (int First, int Last) ResolveWrapGridIndexRange(double top, double bottom)
    {
        var count = ViewModel.ItemCount;

        if (_wrapEdge <= 0 || _wrapPerRow <= 0 || count == 0)
        {
            return (-1, -1);
        }

        var rowCount = (int)Math.Ceiling(count / (double)_wrapPerRow);
        var stride = _wrapEdge + GridSpacing;

        var firstRow = Math.Clamp((int)(top / stride), 0, rowCount - 1);
        var lastRow = Math.Clamp((int)(bottom / stride), 0, rowCount - 1);

        var first = Math.Min(firstRow * _wrapPerRow, count - 1);
        var last = Math.Min(((lastRow + 1) * _wrapPerRow) - 1, count - 1);

        return (first, last);
    }

    /// <summary>取消上次窗口内、本次窗口外的条目的在途解码。</summary>
    private void CancelScrolledOutThumbnails((int First, int Last) previous, int winFirst, int winLast)
    {
        var items = ViewModel.Items;

        // 差集为上次窗口头尾两段：[prevFirst, min(winFirst-1, prevLast)] 与 [max(winLast+1, prevFirst), prevLast]。
        CancelRange(items, previous.First, Math.Min(winFirst - 1, previous.Last));
        CancelRange(items, Math.Max(winLast + 1, previous.First), previous.Last);
    }

    /// <summary>取消闭区间内条目的在途解码；区间无效或越界部分自动收敛。</summary>
    private static void CancelRange(ObservableCollection<MediaItemViewModel> items, int first, int last)
    {
        first = Math.Max(0, first);
        last = Math.Min(items.Count - 1, last);

        for (var i = first; i <= last; i++)
        {
            items[i].CancelPendingLoad();
        }
    }

    /// <summary>方形网格视图加载：登记内建面板与内部滚动条，订阅尺寸变化与触底翻页。</summary>
    private void OnGridViewControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not GridView grid)
        {
            return;
        }

        // 页面加载时方形视图通常处于 Collapsed，模板在首次 measure 前不展开——
        // 此处面板与内部滚动条多半还不存在，登记交给 EnsureGridViewInfrastructure 在
        // 后续容器事件/尺寸变化时惰性补齐；SizeChanged 订阅不依赖模板，先行挂上。
        grid.SizeChanged -= OnGridViewSizeChanged;
        grid.SizeChanged += OnGridViewSizeChanged;

        SubscribeItemPressFeedback(grid);
        EnsureGridViewInfrastructure(grid.ActualWidth);
    }

    /// <summary>视口宽度变化（窗口缩放 / 视图首次变为可见）时重算格子边长。</summary>
    private void OnGridViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 视图从 Collapsed 变可见后模板才展开，内部滚动条此时尚不存在——此处兜底补登记。
        EnsureGridViewInfrastructure(e.NewSize.Width);
    }

    /// <summary>惰性登记方形视图的内建面板与内部滚动条（幂等，可在多个时机反复调用）。</summary>
    /// <param name="viewportWidth">当前视口宽度；未知传 0，仅登记不计算边长。</param>
    private void EnsureGridViewInfrastructure(double viewportWidth)
    {
        _wrapGrid ??= FindDescendant<ItemsWrapGrid>(GridViewControl);

        if (_gridViewer is null && FindDescendant<ScrollViewer>(GridViewControl) is { } viewer)
        {
            _gridViewer = viewer;

            // 先解除再订阅，避免重复登记导致重复订阅。
            viewer.ViewChanged -= OnScrollViewChanged;
            viewer.ViewChanged += OnScrollViewChanged;
        }

        if (viewportWidth > 0)
        {
            UpdateWrapGridCellSize(viewportWidth);
        }
    }

    /// <summary>按视口宽度计算格子边长并写入 ItemsWrapGrid（原生虚拟化的关键配置）。</summary>
    /// <remarks>
    /// 容器占位 = 条目内容边长（档位 + 8 内边距）+ 容器模板 Margin 8（左右合计，相邻容器间隙）。
    /// 每行个数取四舍五入值，容器宽取「可用宽 / 每行个数」恰好填满行宽。以每行个数（perRow）
    /// 为门控：滚动条出现/消失只让宽度小幅变化，perRow 不变时不写 ItemWidth，
    /// 阻断「滚动条 ↔ 边长」布局震荡。
    /// </remarks>
    private void UpdateWrapGridCellSize(double viewportWidth)
    {
        if (_wrapGrid is null || viewportWidth <= 0)
        {
            return;
        }

        var availableWidth = viewportWidth - GridViewHorizontalPadding;
        var target = ViewModel.ThumbnailSize + GridItemPadding + GridSpacing;

        var perRow = Math.Max(1, (int)Math.Round(availableWidth / target));

        if (perRow == _wrapPerRow)
        {
            return;
        }

        _wrapPerRow = perRow;
        _wrapEdge = Math.Max(1, availableWidth / perRow);
        _wrapGrid.ItemWidth = _wrapEdge;
        _wrapGrid.ItemHeight = _wrapEdge;
    }

    /// <summary>
    /// 订阅条目按压反馈：按下缩小、松开回弹（Composition 合成层缩放）。
    /// handledEventsToo 必须为 true——点击复选框时 ButtonBase 会把 PointerPressed 标记为已处理，
    /// 容器的 Pressed 视觉态收不到该按下，只有这里能统一捕获图片与复选框两个入口。
    /// </summary>
    private void SubscribeItemPressFeedback(GridView grid)
    {
        grid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnItemPointerPressed), true);
        grid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnItemPointerReleased), true);
        grid.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnItemPointerReleased), true);
        grid.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnItemPointerReleased), true);

        // 按住移出条目时 Released 的冒泡路径不可靠（可能收不到），用 Move 差异检测 + Exited 兜底：
        // 指针一旦离开被按压条目即提前回弹（照片应用式「移出取消按压」）。
        grid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnItemPointerMoved), true);
        grid.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnItemPointerExited), true);
    }

    private void OnItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FindItemContainer(e.OriginalSource as DependencyObject) is not { } container)
        {
            return;
        }

        _pressedItem = container;
        AnimateItemScale(container, PressedScale, PressSpringDamping);
    }

    private void OnItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedItem is not { } item)
        {
            return;
        }

        _pressedItem = null;
        AnimateItemScale(item, 1f, ReboundSpringDamping);
    }

    private void OnItemPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 按住并移出被按压条目：指针落点已不属于它，立即回弹并解除按压状态。
        if (_pressedItem is not { } item
            || FindItemContainer(e.OriginalSource as DependencyObject) == item)
        {
            return;
        }

        _pressedItem = null;
        AnimateItemScale(item, 1f, ReboundSpringDamping);
    }

    private void OnItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 指针离开 GridView 可视区（Move 不会再触发）：兜底回弹。
        if (_pressedItem is not { } item)
        {
            return;
        }

        _pressedItem = null;
        AnimateItemScale(item, 1f, ReboundSpringDamping);
    }

    /// <summary>以条目中心为原点做合成层弹簧缩放；StartAnimation 自动替换同属性上的前一个动画。</summary>
    private static void AnimateItemScale(GridViewItem item, float target, float dampingRatio)
    {
        var visual = ElementCompositionPreview.GetElementVisual(item);
        visual.CenterPoint = new Vector3((float)(item.ActualWidth / 2), (float)(item.ActualHeight / 2), 0);

        // 弹簧物理动画：加速度平滑收敛（比关键帧直线插值自然），欠阻尼（<1）产生一次柔和回弹。
        var animation = visual.Compositor.CreateSpringScalarAnimation();
        animation.FinalValue = target;
        animation.DampingRatio = dampingRatio;
        animation.Period = SpringPeriod;
        visual.StartAnimation("Scale.X", animation);
        visual.StartAnimation("Scale.Y", animation);
    }

    /// <summary>沿视觉树向上查找条目容器。</summary>
    private static GridViewItem? FindItemContainer(DependencyObject? start)
    {
        while (start is not null)
        {
            if (start is GridViewItem container)
            {
                return container;
            }

            start = VisualTreeHelper.GetParent(start);
        }

        return null;
    }
}
