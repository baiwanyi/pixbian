/**
 * 自适应行式缩略图布局面板（图库「自适应」视图）。
 * 职责：把子项按宽高比贪心分行，行内等比缩放至恰好填满可用宽度，复刻 Windows 照片应用的 Justified 版式。
 * 复用约定：子项容器须为 ContentControl 或 ContentPresenter，其 Content 实现 IAspectRatioItem 提供宽高比；
 *          缺失或非法值一律按 1.0 处理，不抛异常；子项容器自身 Margin 须为 0，间隙统一由 Spacing 承担。
 *          行内整体缩放后实际尺寸会偏离名义行高，故把分配结果回写给实现 IDisplaySizeAware 的条目，
 *          使其按真实显示尺寸请求位图——否则位图按名义尺寸解码后被拉伸就会发虚。
 * 关键约束：本面板不做 UI 虚拟化，条目规模依赖 ViewModel 的分页增量加载控制；
 *          行高围绕 RowHeight 温和波动以精确填满行宽，波动幅度钳制在 [0.5, 1.5] 防止极端；
 *          测量期产出全量行偏移表（RowOffsets/RowStartIndices），页面据此二分求视口覆盖的
 *          索引区间驱动「恢复/取消」——表在下次测量前有效，测量后立刻失效需重取。
 */

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pixbian.Controls;

/// <summary>提供条目宽高比的自适应布局契约。</summary>
public interface IAspectRatioItem
{
    /// <summary>宽高比（宽 / 高）；无法确定时返回 1。</summary>
    double AspectRatio { get; }
}

/// <summary>接收布局面板回写实际显示尺寸的条目契约。</summary>
public interface IDisplaySizeAware
{
    /// <summary>由布局面板回写实际分配到的显示尺寸（逻辑像素）。</summary>
    /// <param name="width">分配宽度。</param>
    /// <param name="height">分配高度。</param>
    void SetDisplaySize(double width, double height);
}

/// <summary>自适应行式布局面板。</summary>
public sealed class JustifiedPanel : Panel
{
    /// <summary>标识 RowHeight 依赖属性：目标行高（像素）。</summary>
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight),
        typeof(double),
        typeof(JustifiedPanel),
        new PropertyMetadata(192.0, OnLayoutPropertyChanged));

    /// <summary>标识 Spacing 依赖属性：相邻子项的间距（像素）。</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(JustifiedPanel),
        new PropertyMetadata(4.0, OnLayoutPropertyChanged));

    private readonly List<Row> _rows = [];
    private readonly HashSet<INotifyPropertyChanged> _subscribed = [];

    /// <summary>每行起始 Y 偏移（长度 = 行数 + 1），measure 期填充，与 _rows 同生命周期。</summary>
    private readonly List<double> _rowOffsets = [];

    /// <summary>每行首个条目的数据索引（长度 = 行数），用于索引 ↔ 行号换算。</summary>
    private readonly List<int> _rowStartIndices = [];

    /// <summary>订阅同步脏标记：仅订阅来源可能变化时重建，避免每次 measure 全量比对。</summary>
    private bool _subscriptionsDirty = true;

    /// <summary>上次订阅同步时的子项数：非虚拟化面板子项只增不减（集合替换时销毁重建），数变化即需重同步。</summary>
    private int _lastSyncedChildCount = -1;

    /// <summary>目标行高（像素）；实际行高等于它乘以行内缩放因子，缩放范围 [0.5, 1.5]。</summary>
    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    /// <summary>相邻子项的间距（像素）。</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>按贪心策略分行并测量各子项。</summary>
    /// <param name="availableSize">可用尺寸；宽度必须有限。</param>
    /// <returns>面板总尺寸。</returns>
    protected override Size MeasureOverride(Size availableSize)
    {
        // 滚动视口在垂直滚动模式下一定给出有限宽度；无限宽兜底为常见窗口宽度。
        var availableWidth = double.IsInfinity(availableSize.Width) ? 800d : availableSize.Width;

        // 订阅同步只在子项集合可能变化时进行（脏标记或数量变化），measure 高频路径零分配。
        if (_subscriptionsDirty || Children.Count != _lastSyncedChildCount)
        {
            SyncItemSubscriptions();
            _lastSyncedChildCount = Children.Count;
            _subscriptionsDirty = false;
        }

        _rows.Clear();
        _rowOffsets.Clear();
        _rowStartIndices.Clear();

        var current = new Row();
        var currentWidth = 0d;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var width = ResolveAspectRatio(child) * RowHeight;

            // 当前行已有内容且再放一项会超宽时封行；保证每行至少一项。
            // 封行时不立即定行高，待收集完所有行后统一计算（末行与非末行的缩放策略不同）。
            if (current.Items.Count > 0 && currentWidth + Spacing + width > availableWidth)
            {
                _rows.Add(current);
                current = new Row();
                currentWidth = 0;
            }

            if (current.Items.Count > 0)
            {
                currentWidth += Spacing;
            }

            if (current.Items.Count == 0)
            {
                current.FirstChildIndex = i;
            }

            current.Items.Add((child, width));
            currentWidth += width;
        }

        if (current.Items.Count > 0)
        {
            _rows.Add(current);
        }

        var offsetY = 0d;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];

            // 行内统一缩放以填满行宽；高度围绕 RowHeight 波动，幅度受 [0.5, 1.5] 钳制。
            row.Complete(Spacing, RowHeight, availableWidth);

            // 行偏移表与行表同步填充：供页面按 Y 二分求视口覆盖的索引区间。
            _rowOffsets.Add(offsetY);
            _rowStartIndices.Add(row.FirstChildIndex);
            row.OffsetY = offsetY;

            foreach (var (child, width) in row.Items)
            {
                child.Measure(new Size(width, row.Height));

                // 回写实际分配尺寸：行内缩放使它与名义行高不同，按名义值解码的位图会被拉伸发虚。
                ApplyDisplaySize(child, width, row.Height);
            }

            offsetY += row.Height;
            if (i < _rows.Count - 1)
            {
                offsetY += Spacing;
            }
        }

        _rowOffsets.Add(offsetY);

        return new Size(availableWidth, offsetY);
    }

    /// <summary>按 Y 区间二分求覆盖的数据索引区间（闭区间）；区间不与任何行相交时返回 (-1, -1)。</summary>
    /// <param name="top">区间上缘（内容坐标，逻辑像素）。</param>
    /// <param name="bottom">区间下缘（内容坐标，逻辑像素）。</param>
    /// <remarks>行偏移表仅在最近一次测量后有效：条目宽高比或面板属性变化引发的下次测量会重建表，
    /// 期间调用方若持有旧区间仅造成一次多余的恢复/取消判定，不产生正确性问题。</remarks>
    internal (int First, int Last) IndexRangeFromY(double top, double bottom)
    {
        var firstRow = FindFirstRowEndingAfter(top);

        if (firstRow < 0)
        {
            return (-1, -1);
        }

        // bottom 超出内容总高时夹到最后一行。
        var lastRow = FindFirstRowEndingAfter(bottom);
        if (lastRow < 0)
        {
            lastRow = _rowStartIndices.Count - 1;
        }

        var first = _rowStartIndices[firstRow];
        var last = lastRow + 1 < _rowStartIndices.Count
            ? _rowStartIndices[lastRow + 1] - 1
            : _rowStartIndices[^1] + _rows[^1].Items.Count - 1;

        return (first, last);
    }

    /// <summary>二分查找第一个底边严格越过 y 的行；y 不低于内容总高时返回 -1。</summary>
    private int FindFirstRowEndingAfter(double y)
    {
        var count = _rowStartIndices.Count;

        if (count == 0 || y >= _rowOffsets[count])
        {
            return -1;
        }

        int lo = 0, hi = count - 1;

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;

            if (_rowOffsets[mid + 1] > y)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    /// <summary>按测量阶段生成的行布局排列各子项。</summary>
    /// <param name="finalSize">最终尺寸。</param>
    /// <returns>实际占用尺寸。</returns>
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var row in _rows)
        {
            var x = 0d;

            foreach (var (child, width) in row.Items)
            {
                child.Arrange(new Rect(x, row.OffsetY, width, row.Height));
                x += width + Spacing;
            }
        }

        return finalSize;
    }

    /// <summary>
    /// 从子项容器解析宽高比：优先使用 FrameworkElement.DataContext，再回落 ContentControl.Content。
    /// 无分组 GridView 中子项 Content 是 DataTemplate 根（Border），只有 DataContext 才是 ViewModel。
    /// </summary>
    /// <param name="child">子项容器。</param>
    /// <returns>宽高比。</returns>
    private static double ResolveAspectRatio(UIElement child)
    {
        double ratio = 1.0;

        if (child is FrameworkElement { DataContext: IAspectRatioItem vm } && vm.AspectRatio > 0)
        {
            ratio = vm.AspectRatio;
        }
        else if (child is ContentControl { Content: IAspectRatioItem item } && item.AspectRatio > 0)
        {
            ratio = item.AspectRatio;
        }

        return ratio;
    }

    /// <summary>把实际分配到的显示尺寸回写给条目，使其按真实尺寸请求缩略图。</summary>
    /// <param name="child">子项容器。</param>
    /// <param name="width">分配宽度。</param>
    /// <param name="height">分配高度。</param>
    private static void ApplyDisplaySize(UIElement child, double width, double height)
    {
        if (child is FrameworkElement { DataContext: IDisplaySizeAware target })
        {
            target.SetDisplaySize(width, height);
        }
    }

    /// <summary>订阅当前子项的属性变更、清理已不在子项集合中的通知源，确保宽高比变化触发重测。</summary>
    /// <remarks>仅在脏标记或子项数变化时调用（见 MeasureOverride）；非虚拟化面板子项只增不减，
    /// 集合替换时容器销毁重建会使数量归零再增长，天然覆盖「旧订阅清理」需求。</remarks>
    private void SyncItemSubscriptions()
    {
        var current = new HashSet<INotifyPropertyChanged>();

        foreach (var child in Children)
        {
            if (child is FrameworkElement { DataContext: INotifyPropertyChanged notifier })
            {
                current.Add(notifier);
            }
        }

        foreach (var stale in _subscribed.Except(current).ToList())
        {
            stale.PropertyChanged -= OnItemPropertyChanged;
            _subscribed.Remove(stale);
        }

        foreach (var notifier in current)
        {
            if (_subscribed.Add(notifier))
            {
                notifier.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    /// <summary>子项宽高比变化时，标记面板需要重新测量。</summary>
    /// <remarks>
    /// 只响应宽高比、不响应位图（Thumbnail）：布局几何仅由宽高比决定，位图替换不改变任何几何。
    /// 若监听位图，每张缩略图解码完成都会触发整面板重测——本面板不做虚拟化，
    /// 200 条全量重排 × 200 张逐个到位 = 数万次测量，且重测中回写的显示尺寸会再触发升级解码，
    /// 与位图到达形成「解码 → 重测 → 回写 → 再解码」的正反馈，是布局循环（LayoutCycleException）
    /// 的直接温床。宽高比变化的通知已含全部几何信息，由其单独驱动重排即可。
    /// </remarks>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(global::Pixbian.ViewModels.MediaItemViewModel.AspectRatio))
        {
            InvalidateMeasure();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is JustifiedPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    /// <summary>单行的布局数据：成员、行高与垂直偏移。</summary>
    private sealed class Row
    {
        public List<(UIElement Child, double Width)> Items { get; } = [];

        /// <summary>行首成员在 Children 中的索引，分行阶段填充，供行偏移表换算数据索引。</summary>
        public int FirstChildIndex { get; set; }

        /// <summary>行高，由行内缩放因子与目标行高相乘得出。</summary>
        public double Height { get; private set; }

        public double OffsetY { get; set; }

        /// <summary>按可用宽度计算行内缩放因子并确定最终行高与各成员宽度。</summary>
        /// <param name="spacing">子项间距。</param>
        /// <param name="targetHeight">目标行高。</param>
        /// <param name="availableWidth">可用宽度。</param>
        public void Complete(double spacing, double targetHeight, double availableWidth)
        {
            if (Items.Count == 0)
            {
                return;
            }

            var gaps = (Items.Count - 1) * spacing;
            var aspectSum = 0d;

            foreach (var (_, width) in Items)
            {
                aspectSum += width / targetHeight;
            }

            // 缩放因子 = 可用净宽 / 行内自然净宽；钳制在 [0.5, 1.5] 避免行高剧烈波动。
            var scale = aspectSum > 0
                ? Math.Clamp((availableWidth - gaps) / (aspectSum * targetHeight), 0.5, 1.5)
                : 1.0;

            Height = targetHeight * scale;

            for (var i = 0; i < Items.Count; i++)
            {
                var (child, width) = Items[i];
                Items[i] = (child, (width / targetHeight) * Height);
            }
        }
    }
}
