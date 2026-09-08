/**
 * 等高行式虚拟化布局（图库「自适应」视图 P2 核心改造）。
 * 职责：把 JustifiedPanel 的贪心分行算法迁入 VirtualizingLayout——行几何表（行偏移、
 *      行首索引、行高、条目位置）一次建表 O(n)，按 RealizationRect 二分只 realize
 *      视口覆盖的行，切换目录与滚动的容器/测量成本从 O(集合) 降为 O(视口)。
 * 复用约定：条目须实现 IAspectRatioItem 提供宽高比（经 GetItemAt 读取，缺失按 1.0）；
 *          realize 的条目在排列时回写实际显示尺寸（IDisplaySizeAware），与 JustifiedPanel
 *          同一契约；行内缩放因子钳制 [0.5, 1.5]，末行同样拉伸填满行宽。
 * 关键约束：行表按「集合版本 / 可用宽度 / 脏标记」失效重建，重建为 O(n) 纯 CLR 读取；
 *          VirtualizingLayout 无条目 INPC 订阅机制，宽高比异步写回后须由调用方调
 *          InvalidateRows 触发重建；未 realize 行按已建行表参与 Extent（无估算抖动）；
 *          GetOrCreateElementAt 用默认选项，滚出 RealizationRect 的元素由宿主自动回收；
 *          Arrange 复用 measure 阶段记录的 realized 映射（VirtualizingLayoutContext 无
 *          GetElementAt，经 context.LayoutState 跨阶段传递）。
 */

using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Services;
using Windows.Foundation;

namespace Pixbian.Controls;

/// <summary>等高行式虚拟化布局。</summary>
public sealed class JustifiedVirtualizingLayout : VirtualizingLayout
{
    /// <summary>标识 RowHeight 依赖属性：目标行高（像素）。</summary>
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight),
        typeof(double),
        typeof(JustifiedVirtualizingLayout),
        new PropertyMetadata(192.0, OnLayoutPropertyChanged));

    /// <summary>标识 Spacing 依赖属性：相邻子项的间距（像素）。</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(JustifiedVirtualizingLayout),
        new PropertyMetadata(4.0, OnLayoutPropertyChanged));

    /// <summary>外部失效标记（宽高比写回 / 属性变化）：下次 measure 重建行表。</summary>
    private bool _pendingRebuild;

    /// <summary>当前宿主关联的行几何表（Initialize 时缓存），供 IndexRangeFromY 查询。</summary>
    private RowTableState? LayoutContextCurrent;

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

    /// <summary>行几何表失效（宽高比批量写回后由页面调用）：下次 measure 重建行表。</summary>
    public void InvalidateRows()
    {
        _pendingRebuild = true;
        InvalidateMeasure();
    }

    /// <summary>按 Y 区间二分求覆盖的数据索引区间（闭区间）；行表未就绪或无覆盖返回 (-1, -1)。</summary>
    /// <remarks>行表内容与上次 measure 一致；首次 measure 前调用无效。供页面滚动驱动
    /// 调度器的视口窗口化（与 JustifiedPanel 同签名同语义）。</remarks>
    public (int First, int Last) IndexRangeFromY(double top, double bottom)
    {
        if (LayoutContextCurrent is not RowTableState state)
        {
            return (-1, -1);
        }

        return state.GetRealizedRange(new Rect(0, top, state.AvailableWidth, Math.Max(0, bottom - top)));
    }

    /// <inheritdoc />
    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
    {
        context.LayoutState = new RowTableState();
        LayoutContextCurrent = (RowTableState)context.LayoutState;
    }

    /// <inheritdoc />
    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        context.LayoutState = null;
        LayoutContextCurrent = null;
    }

    /// <summary>测量：按需重建行几何表，realize 视口覆盖行的条目并按行表尺寸测量。</summary>
    /// <remarks>布局 pass 内的托管异常在 XAML 中表现为 fail-fast（0xc000027b，无托管堆栈），
    /// 临时以 try/catch 捕获并记录细节（TempTiming）后回退估算值——定位完成后移除。</remarks>
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            return MeasureOverrideCore(context, availableSize);
        }
        catch (Exception ex)
        {
            TempTiming.Log(
                $"LAYOUT|measure|fail|{ex.GetType().Name}|{ex.Message}"
                + $"|{ex.StackTrace?.Replace('\r', ' ').Replace('\n', ' ')}");

            if (context.LayoutState is RowTableState fallback)
            {
                return new Size(double.IsInfinity(availableSize.Width) ? 800d : availableSize.Width, fallback.TotalHeight);
            }

            return new Size(double.IsInfinity(availableSize.Width) ? 800d : availableSize.Height, 0);
        }
    }

    /// <summary>测量核心实现。</summary>
    private Size MeasureOverrideCore(VirtualizingLayoutContext context, Size availableSize)
    {
        // 垂直滚动模式下宽度必然有限；无限宽兜底为常见窗口宽度（与 JustifiedPanel 一致）。
        var availableWidth = double.IsInfinity(availableSize.Width) ? 800d : availableSize.Width;
        var state = GetState(context);

        if (state.IsDirty
            || _pendingRebuild
            || Math.Abs(state.AvailableWidth - availableWidth) > 0.5
            || state.Count != context.ItemCount)
        {
            _pendingRebuild = false;
            RebuildRows(context, state, availableWidth);
        }

        var (firstIndex, lastIndex) = state.GetRealizedRange(context.RealizationRect);

        // measure 阶段记录 realized 映射供 Arrange 复用（pass 内有效）。
        state.RealizedElements.Clear();

        // RealizationRect 为空（宿主视口未就绪）时无覆盖区间，跳过 realize——
        // 负索引传入 GetOrCreateElementAt 会在 WinRT ABI 层回绕成超大无符号数而崩溃。
        if (firstIndex >= 0)
        {
            for (var i = firstIndex; i <= lastIndex; i++)
            {
                var child = context.GetOrCreateElementAt(i);
                child.Measure(new Size(state.ItemWidths[i], state.RowHeightOf(i)));
                state.RealizedElements[i] = child;
            }
        }

        return new Size(availableWidth, state.TotalHeight);
    }

    /// <summary>排列：按行表位置排列本 pass realize 的条目，并回写实际显示尺寸。</summary>
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        try
        {
            var state = GetState(context);

            foreach (var (index, child) in state.RealizedElements)
            {
                child.Arrange(new Rect(state.ItemXs[index], state.ItemYs[index], state.ItemWidths[index], state.ItemHeightOf(index)));

                // 回写实际分配尺寸：行内缩放使它与名义行高不同，按名义值解码的位图会被拉伸发虚。
                if (child is FrameworkElement { DataContext: IDisplaySizeAware target })
                {
                    target.SetDisplaySize(state.ItemWidths[index], state.ItemHeightOf(index));
                }
            }

            return finalSize;
        }
        catch (Exception ex)
        {
            TempTiming.Log(
                $"LAYOUT|arrange|fail|{ex.GetType().Name}|{ex.Message}"
                + $"|{ex.StackTrace?.Replace('\r', ' ').Replace('\n', ' ')}");

            return finalSize;
        }
    }

    /// <inheritdoc />
    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        if (context.LayoutState is RowTableState state)
        {
            state.IsDirty = true;
        }

        InvalidateMeasure();
    }

    /// <summary>取本布局的行几何表状态。</summary>
    private static RowTableState GetState(VirtualizingLayoutContext context) =>
        context.LayoutState as RowTableState ?? throw new InvalidOperationException("行几何表状态未初始化。");

    /// <summary>重建行几何表：贪心分行，封行时即定行高并回填行内成员位置（单遍 O(n)）。</summary>
    private void RebuildRows(VirtualizingLayoutContext context, RowTableState state, double availableWidth)
    {
        var count = context.ItemCount;

        state.Reset(count);
        state.AvailableWidth = availableWidth;
        state.Count = count;

        // 当前行成员的宽高比前缀和：prefix[j] = 前 j 个成员的宽高比之和（prefix[0] = 0）。
        var prefix = new List<double>(Math.Min(count, 64)) { 0 };
        var rowStart = 0;
        var offsetY = 0d;

        for (var i = 0; i < count; i++)
        {
            var ratio = ResolveRatio(context.GetItemAt(i));
            var width = ratio * RowHeight;
            var rowNaturalWidth = (prefix[^1] * RowHeight) + ((prefix.Count - 1) * Spacing);

            // 当前行已有成员且再放一项超宽时封行；保证每行至少一项。
            if (prefix.Count > 1 && rowNaturalWidth + Spacing + width > availableWidth)
            {
                offsetY += CloseRow(state, rowStart, prefix, offsetY);
                rowStart = i;
                prefix.Clear();
                prefix.Add(0);
            }

            prefix.Add(prefix[^1] + ratio);
        }

        if (prefix.Count > 1)
        {
            offsetY += CloseRow(state, rowStart, prefix, offsetY);
        }

        // 末尾哨兵（内容总高）：行数为 n 时 RowOffsets 须有 n+1 项，二分查询依赖 RowOffsets[count]。
        state.RowOffsets.Add(offsetY);
        state.TotalHeight = offsetY;
        state.IsDirty = false;
    }

    /// <summary>封行：按行内缩放因子定行高，回填行内全部成员的位置与宽度，返回本行占用高度（含间距）。</summary>
    /// <param name="state">行几何表。</param>
    /// <param name="rowStart">行首数据索引。</param>
    /// <param name="prefix">行成员宽高比前缀和（长度 = 成员数 + 1）。</param>
    /// <param name="offsetY">行起始 Y。</param>
    private double CloseRow(RowTableState state, int rowStart, List<double> prefix, double offsetY)
    {
        var memberCount = prefix.Count - 1;
        var aspectSum = prefix[^1];
        var gaps = (memberCount - 1) * Spacing;

        // 缩放因子 = 可用净宽 / 行内名义净宽；钳制在 [0.5, 1.5] 避免行高剧烈波动。
        var scale = aspectSum > 0
            ? Math.Clamp((state.AvailableWidth - gaps) / (aspectSum * RowHeight), 0.5, 1.5)
            : 1.0;
        var rowHeight = RowHeight * scale;

        state.RowOffsets.Add(offsetY);
        state.RowStartIndices.Add(rowStart);
        state.RowHeights.Add(rowHeight);

        for (var j = 0; j < memberCount; j++)
        {
            // 行按数据顺序封行、成员按序回填，Add 的追加序即数据索引序；
            // EnsureCapacity 只扩容 Capacity 不加 Count，此处必须 Add 而非索引赋值。
            state.ItemXs.Add((j * Spacing) + (prefix[j] * rowHeight));
            state.ItemYs.Add(offsetY);
            state.ItemWidths.Add((prefix[j + 1] - prefix[j]) * rowHeight);
        }

        return rowHeight + Spacing;
    }

    /// <summary>从条目解析宽高比：非正或缺失一律按 1.0（与 JustifiedPanel 同策略）。</summary>
    private static double ResolveRatio(object? item) =>
        item is IAspectRatioItem { AspectRatio: > 0 } provider ? provider.AspectRatio : 1.0;

    /// <summary>行高或间距属性变化：行表失效并重测。</summary>
    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is JustifiedVirtualizingLayout layout)
        {
            layout._pendingRebuild = true;
            layout.InvalidateMeasure();
        }
    }

    /// <summary>行几何表：行偏移 / 行首索引 / 行高 / 成员位置，挂在 LayoutState 上跨 pass 保留。</summary>
    private sealed class RowTableState
    {
        public bool IsDirty = true;
        public double AvailableWidth;
        public int Count = -1;
        public double TotalHeight;

        public readonly List<double> RowOffsets = [];
        public readonly List<int> RowStartIndices = [];
        public readonly List<double> RowHeights = [];
        public readonly List<double> ItemXs = [];
        public readonly List<double> ItemYs = [];
        public readonly List<double> ItemWidths = [];

        /// <summary>本 measure pass realize 的元素映射：索引 → 元素（Arrange 复用）。</summary>
        public readonly Dictionary<int, UIElement> RealizedElements = [];

        /// <summary>行表容量重置（条目规模已知，按需扩容）。</summary>
        public void Reset(int count)
        {
            RowOffsets.Clear();
            RowStartIndices.Clear();
            RowHeights.Clear();
            ItemXs.Clear();
            ItemYs.Clear();
            ItemWidths.Clear();
            RealizedElements.Clear();

            if (count > 0)
            {
                ItemXs.EnsureCapacity(count);
                ItemYs.EnsureCapacity(count);
                ItemWidths.EnsureCapacity(count);
            }
        }

        /// <summary>条目所在行高。</summary>
        public double RowHeightOf(int index)
        {
            var row = RowIndexFromItem(index);
            return RowHeights[row];
        }

        /// <summary>条目高度（等于所在行高，与宽度同源）。</summary>
        public double ItemHeightOf(int index) => RowHeightOf(index);

        /// <summary>二分求条目所在行号。</summary>
        private int RowIndexFromItem(int index)
        {
            int lo = 0, hi = RowStartIndices.Count - 1;

            while (lo < hi)
            {
                var mid = (lo + hi) / 2;

                if (RowStartIndices[mid + 1] > index)
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

        /// <summary>求 RealizationRect 覆盖的数据索引区间（闭区间）；无覆盖返回 (-1, -1)。</summary>
        public (int First, int Last) GetRealizedRange(Rect rect)
        {
            var rowCount = RowStartIndices.Count;

            if (rowCount == 0 || rect.Height <= 0)
            {
                return (-1, -1);
            }

            var firstRow = FindFirstRowEndingAfter(rect.Top);
            var lastRow = FindFirstRowEndingAfter(rect.Bottom);

            if (firstRow < 0)
            {
                return (-1, -1);
            }

            if (lastRow < 0)
            {
                lastRow = rowCount - 1;
            }

            var first = RowStartIndices[firstRow];
            var last = lastRow + 1 < rowCount
                ? RowStartIndices[lastRow + 1] - 1
                : RowStartIndices[^1] + MemberCount(lastRow) - 1;

            return (first, last);
        }

        /// <summary>行内成员数。</summary>
        private int MemberCount(int row) =>
            row + 1 < RowStartIndices.Count
                ? RowStartIndices[row + 1] - RowStartIndices[row]
                : ItemWidths.Count - RowStartIndices[row];

        /// <summary>二分查找第一个底边严格越过 y 的行；y 不低于内容总高时返回 -1。</summary>
        private int FindFirstRowEndingAfter(double y)
        {
            var count = RowStartIndices.Count;

            if (count == 0 || y >= RowOffsets[count])
            {
                return -1;
            }

            int lo = 0, hi = count - 1;

            while (lo < hi)
            {
                var mid = (lo + hi) / 2;

                if (RowOffsets[mid + 1] > y)
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
    }
}
