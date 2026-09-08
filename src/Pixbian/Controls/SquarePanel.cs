/**
 * 正方形网格布局面板（图库「方形」视图）。
 * 职责：把子项排成边长一致的正方形网格，每行个数由目标边长与可用宽度推导，
 *       行内格子均匀放大至恰好填满行宽，行末不残留目标尺寸量级的空隙。
 * 复用约定：子项容器自身 Margin 须为 0，间隙统一由 Spacing 承担；条目 ViewModel 须实现
 *       IDisplaySizeAware，测量时回写实际边长以驱动缩略图按真实尺寸解码（与 JustifiedPanel 同一契约）。
 * 关键约束：本面板不做 UI 虚拟化，条目规模依赖 ViewModel 的分页增量加载控制（与 JustifiedPanel 一致）；
 *       仅适用于非分组 GridView（分组容器会让面板拿到 GroupItem 而非条目容器）；
 *       测量阶段算出的行参数缓存在字段中供排列阶段复用，两阶段不重算以免结果不一致；
 *       行参数同时对外提供按 Y 区间求索引区间的查询，供页面驱动瘦身恢复与在途取消。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pixbian.Controls;

/// <summary>正方形网格布局面板。</summary>
public sealed class SquarePanel : Panel
{
    /// <summary>标识 ItemSize 依赖属性：目标边长（像素），实际边长围绕它取整放大以填满行宽。</summary>
    public static readonly DependencyProperty ItemSizeProperty = DependencyProperty.Register(
        nameof(ItemSize),
        typeof(double),
        typeof(SquarePanel),
        new PropertyMetadata(192.0, OnLayoutPropertyChanged));

    /// <summary>标识 Spacing 依赖属性：相邻子项的间距（像素）。</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(SquarePanel),
        new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    /// <summary>测量阶段算出的每行个数，排列阶段直接复用。</summary>
    private int _perRowCount;

    /// <summary>测量阶段算出的格子边长，排列阶段直接复用。</summary>
    private double _edgeLength;

    /// <summary>目标边长（像素）。</summary>
    public double ItemSize
    {
        get => (double)GetValue(ItemSizeProperty);
        set => SetValue(ItemSizeProperty, value);
    }

    /// <summary>相邻子项的间距（像素）。</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>按目标边长推导每行个数，以恰好填满行宽的边长测量各子项。</summary>
    /// <param name="availableSize">可用尺寸；宽度必须有限。</param>
    /// <returns>面板总尺寸。</returns>
    protected override Size MeasureOverride(Size availableSize)
    {
        // 滚动视口在垂直滚动模式下一定给出有限宽度；无限宽兜底为常见窗口宽度。
        var availableWidth = double.IsInfinity(availableSize.Width) ? 800d : availableSize.Width;

        if (Children.Count == 0)
        {
            return new Size(availableWidth, 0);
        }

        // 每行个数取「可用宽 / 目标占位」的四舍五入值：向下取整会让格子放大更多，
        // 四舍五入使实际边长与目标值的偏差最小，同时保证行内无目标尺寸量级的行末空隙。
        _perRowCount = Math.Max(1, (int)Math.Round((availableWidth + Spacing) / (ItemSize + Spacing)));
        _edgeLength = (availableWidth - (Spacing * (_perRowCount - 1))) / _perRowCount;

        foreach (var child in Children)
        {
            child.Measure(new Size(_edgeLength, _edgeLength));

            // 回写实际边长：窗口缩放会使边长偏离档位值，按名义值解码的位图会被拉伸发虚；
            // 同值重复回写由 ViewModel 的显示尺寸容差去重，不会引发反复重解码。
            ApplyDisplaySize(child, _edgeLength);
        }

        var rowCount = (int)Math.Ceiling(Children.Count / (double)_perRowCount);
        var totalHeight = (rowCount * _edgeLength) + ((rowCount - 1) * Spacing);
        return new Size(availableWidth, totalHeight);
    }

    /// <summary>按 Y 区间求覆盖的数据索引区间（闭区间）；无子项时返回 (-1, -1)。</summary>
    /// <param name="top">区间上缘（内容坐标，逻辑像素）。</param>
    /// <param name="bottom">区间下缘（内容坐标，逻辑像素）。</param>
    /// <remarks>行高均匀，除法直取行号即可；行距边界落在间隙时按向下取整归入上一行，
    /// 区间可能比严格覆盖多出一行，多余的恢复/取消判定无副作用。</remarks>
    internal (int First, int Last) IndexRangeFromY(double top, double bottom)
    {
        if (Children.Count == 0)
        {
            return (-1, -1);
        }

        var rowStride = _edgeLength + Spacing;

        if (rowStride <= 0)
        {
            return (-1, -1);
        }

        var rowCount = (int)Math.Ceiling(Children.Count / (double)_perRowCount);
        var firstRow = Math.Clamp((int)(top / rowStride), 0, rowCount - 1);
        var lastRow = Math.Clamp((int)(bottom / rowStride), 0, rowCount - 1);

        var first = firstRow * _perRowCount;
        var last = Math.Min(Children.Count - 1, ((lastRow + 1) * _perRowCount) - 1);

        return (first, last);
    }

    /// <summary>按测量阶段缓存的行参数排列各子项；末行不足一行时左对齐。</summary>
    /// <param name="finalSize">最终尺寸。</param>
    /// <returns>实际占用尺寸。</returns>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var column = 0;

        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, y, _edgeLength, _edgeLength));
            x += _edgeLength + Spacing;
            column++;

            if (column == _perRowCount)
            {
                column = 0;
                x = 0;
                y += _edgeLength + Spacing;
            }
        }

        return finalSize;
    }

    /// <summary>把实际分配到的边长回写给条目，使其按真实尺寸请求缩略图。</summary>
    /// <param name="child">子项容器。</param>
    /// <param name="edge">分配到的格子边长。</param>
    private static void ApplyDisplaySize(UIElement child, double edge)
    {
        if (child is FrameworkElement { DataContext: IDisplaySizeAware target })
        {
            target.SetDisplaySize(edge, edge);
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is SquarePanel panel)
        {
            panel.InvalidateMeasure();
        }
    }
}
