/**
 * 等高视图选择服务（图库 P2：ItemsRepeater 无内建选择机制的补位件）。
 * 职责：以条目引用维护等高视图的选中集合，把选中态回写到条目 VM 的 IsSelected
 *      （模板直绑显示），提供单击 / Toggle / 范围 / 全选操作并经事件通知页面聚合。
 * 复用约定：索引语义由条目引用承载（删除/替换不易悬空），范围操作经 Items 定位锚点；
 *      回写 IsSelected 必须在 UI 线程（调用方保证——所有入口都由页面交互事件发起）。
 * 关键约束：集合替换（切目录）与条目删除时调用方必须 Clear / Remove，避免悬空引用；
 *      本服务不持有视觉元素，选中态的视觉呈现完全经 IsSelected 属性通知驱动，
 *      回收重建的容器天然显示最新选中态。
 */

namespace Pixbian.ViewModels;

/// <summary>等高视图选择服务。</summary>
public sealed class GallerySelectionService
{
    private readonly Func<IReadOnlyList<MediaItemViewModel>> _itemsProvider;
    private readonly HashSet<MediaItemViewModel> _selected = [];

    /// <summary>Shift 范围选择的锚点条目（最近一次单击/选择的条目）。</summary>
    private MediaItemViewModel? _anchor;

    /// <summary>选中集合变化时触发（UI 线程）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>初始化服务；条目提供者用于范围与全选操作。</summary>
    public GallerySelectionService(Func<IReadOnlyList<MediaItemViewModel>> itemsProvider)
    {
        ArgumentNullException.ThrowIfNull(itemsProvider);
        _itemsProvider = itemsProvider;
    }

    /// <summary>当前选中条目（按集合顺序）。</summary>
    public IReadOnlyList<MediaItemViewModel> SelectedItems =>
        _itemsProvider().Where(_selected.Contains).ToList();

    /// <summary>条目是否被选中。</summary>
    public bool IsSelected(MediaItemViewModel item) => _selected.Contains(item);

    /// <summary>切换单个条目的选中态（选择模式单击 / 复选框）。</summary>
    public void Toggle(MediaItemViewModel item)
    {
        if (!_selected.Remove(item))
        {
            _selected.Add(item);
            item.IsSelected = true;
            _anchor = item;
        }
        else
        {
            item.IsSelected = false;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>单选语义：清空其余并只选中该条目（选择模式普通单击，预留）。</summary>
    public void Select(MediaItemViewModel item)
    {
        ApplySelectionDiff([item]);
        _anchor = item;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>范围选择：从锚点条目到目标条目的连续区间全部选中（Shift + 单击）。</summary>
    public void SelectRangeTo(MediaItemViewModel item)
    {
        var items = _itemsProvider();
        var from = _anchor is null ? -1 : IndexOf(items, _anchor);
        var to = IndexOf(items, item);

        if (from < 0 || to < 0)
        {
            // 锚点失效（已滚出被淘汰不在集合）退化为单选。
            Select(item);
            return;
        }

        var start = Math.Min(from, to);
        var end = Math.Max(from, to);
        var range = new List<MediaItemViewModel>(end - start + 1);

        for (var i = start; i <= end; i++)
        {
            range.Add(items[i]);
        }

        ApplySelectionDiff(range);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>全选。</summary>
    public void SelectAll()
    {
        ApplySelectionDiff([.. _itemsProvider()]);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空选择。</summary>
    public void Clear()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        foreach (var item in _selected)
        {
            item.IsSelected = false;
        }

        _selected.Clear();
        _anchor = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>条目移出集合（删除）时解除登记，避免悬空引用。</summary>
    public void Remove(MediaItemViewModel item)
    {
        if (_selected.Remove(item))
        {
            item.IsSelected = false;
        }

        if (ReferenceEquals(_anchor, item))
        {
            _anchor = null;
        }
    }

    /// <summary>把选中集合对齐到目标集合：增删的条目回写 IsSelected，其余不动。</summary>
    private void ApplySelectionDiff(IReadOnlyList<MediaItemViewModel> target)
    {
        var targetSet = new HashSet<MediaItemViewModel>(target);

        foreach (var item in _selected)
        {
            if (!targetSet.Contains(item))
            {
                item.IsSelected = false;
            }
        }

        foreach (var item in target)
        {
            item.IsSelected = true;
        }

        _selected.Clear();
        _selected.UnionWith(target);
    }

    /// <summary>在只读列表中按引用定位条目索引；不存在返回 -1。</summary>
    private static int IndexOf(IReadOnlyList<MediaItemViewModel> items, MediaItemViewModel item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
