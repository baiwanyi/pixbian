/**
 * 等高视图选择服务的单元测试。
 * 职责：锁定选择服务的核心语义——Toggle 增量写、游离条目剔除（交集陷阱）、
 *       范围/全选的对齐写、锚点生命周期与 SelectionChanged 触发规则。
 * 复用约定：条目 VM 用真实 MediaItemViewModel 构造（loader 恒返 null，不触碰位图），
 *          集合以可变 List 承载以模拟切目录与删除。
 * 关键约束：游离条目用例必须保留——SelectedItems 是选中集与当前集合的交集，
 *          残留引用会让页面误判「无选中」而退出选择模式（历史真实缺陷）。
 */

using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Controls;
using Pixbian.ViewModels;
using Xunit;

namespace Pixbian.UI.Tests.ViewModels;

/// <summary>GallerySelectionService 测试。</summary>
public sealed class GallerySelectionServiceTests
{
    private readonly List<MediaItemViewModel> _items = [];
    private readonly GallerySelectionService _service;

    public GallerySelectionServiceTests()
    {
        _service = new GallerySelectionService(() => _items);
        _items.AddRange(Enumerable.Range(0, 6).Select(i => CreateItem(i)));
    }

    private static MediaItemViewModel CreateItem(long id) => new(
        new MediaItem
        {
            Id = id,
            Path = $"D:\\Lib\\{id}.jpg",
            FileName = $"{id}.jpg",
            Kind = MediaKind.Image
        },
        (_, _, _) => Task.FromResult<BitmapImage?>(null));

    private int ChangedCount
    {
        get { return _changedCount; }
    }

    private int _changedCount;

    private void CountChanges(object? sender, EventArgs e) => _changedCount++;

    [Fact]
    public void Toggle_未选中条目_登记选中并回写IsSelected()
    {
        _service.SelectionChanged += CountChanges;

        _service.Toggle(_items[2]);

        Assert.True(_service.IsSelected(_items[2]));
        Assert.True(_items[2].IsSelected);
        Assert.Single(_service.SelectedItems);
        Assert.Equal(1, ChangedCount);
    }

    [Fact]
    public void Toggle_已选中条目_取消选中()
    {
        _service.Toggle(_items[2]);

        _service.Toggle(_items[2]);

        Assert.False(_service.IsSelected(_items[2]));
        Assert.False(_items[2].IsSelected);
        Assert.Empty(_service.SelectedItems);
    }

    [Fact]
    public void Toggle_游离条目_拒绝登记且不污染计数()
    {
        // 交集陷阱：不属于当前集合的条目一旦登记，永远进不了 SelectedItems，
        // 却让选中计数虚增、页面误判。历史真实缺陷，用例必须保留。
        var stranger = CreateItem(99);

        _service.Toggle(stranger);

        Assert.Empty(_service.SelectedItems);
        Assert.False(stranger.IsSelected);
        Assert.False(_service.IsSelected(stranger));
    }

    [Fact]
    public void Toggle_集合替换后的残留条目_被清理且复位选中态()
    {
        _service.Toggle(_items[0]);
        _service.Toggle(_items[1]);

        // 模拟切目录：集合整体替换为不含任何旧条目的新列表。
        _items.Clear();
        _items.AddRange(Enumerable.Range(10, 3).Select(i => CreateItem(i)));

        _service.Toggle(_items[0]);

        Assert.Single(_service.SelectedItems);
        Assert.Same(_items[0], _service.SelectedItems[0]);
    }

    [Fact]
    public void Select_单选语义_清空其余只留该条目()
    {
        _service.Toggle(_items[0]);
        _service.Toggle(_items[1]);

        _service.Select(_items[3]);

        var selected = _service.SelectedItems;
        Assert.Single(selected);
        Assert.Same(_items[3], selected[0]);
        Assert.False(_items[0].IsSelected);
        Assert.False(_items[1].IsSelected);
    }

    [Fact]
    public void SelectRangeTo_锚点在前_连续区间全选()
    {
        _service.Toggle(_items[1]);

        _service.SelectRangeTo(_items[4]);

        Assert.Equal(4, _service.SelectedItems.Count);
        Assert.All(_items.Skip(1).Take(4), item => Assert.True(item.IsSelected));
    }

    [Fact]
    public void SelectRangeTo_锚点在后_反向区间同样全选()
    {
        _service.Toggle(_items[4]);

        _service.SelectRangeTo(_items[1]);

        Assert.Equal(4, _service.SelectedItems.Count);
        Assert.All(_items.Skip(1).Take(4), item => Assert.True(item.IsSelected));
    }

    [Fact]
    public void SelectRangeTo_锚点已不在集合_退化为单选()
    {
        var anchor = CreateItem(50);
        _service.Select(anchor);

        // 集合替换使锚点失效。
        _items.Clear();
        _items.AddRange(Enumerable.Range(20, 4).Select(i => CreateItem(i)));

        _service.SelectRangeTo(_items[2]);

        Assert.Single(_service.SelectedItems);
        Assert.Same(_items[2], _service.SelectedItems[0]);
    }

    [Fact]
    public void SelectAll_全选()
    {
        _service.SelectAll();

        Assert.Equal(_items.Count, _service.SelectedItems.Count);
        Assert.All(_items, item => Assert.True(item.IsSelected));
    }

    [Fact]
    public void Clear_清空选中并复位IsSelected与锚点()
    {
        _service.Toggle(_items[1]);
        _service.Toggle(_items[2]);
        _service.Clear();

        _service.SelectRangeTo(_items[4]);

        // 锚点已被 Clear 复位：范围选择只覆盖目标条目自身。
        Assert.Single(_service.SelectedItems);
        Assert.All(_items, item => Assert.Equal(ReferenceEquals(item, _items[4]), item.IsSelected));
    }

    [Fact]
    public void Clear_无选中时不触发事件()
    {
        _service.SelectionChanged += CountChanges;

        _service.Clear();

        Assert.Equal(0, ChangedCount);
    }

    [Fact]
    public void Remove_选中条目_解除登记触发事件并复位锚点()
    {
        _service.Toggle(_items[2]);
        _service.SelectionChanged += CountChanges;

        _service.Remove(_items[2]);

        Assert.Empty(_service.SelectedItems);
        Assert.Equal(1, ChangedCount);

        // 锚点已复位：随后的范围选择退化为单选。
        _service.SelectRangeTo(_items[4]);
        Assert.Single(_service.SelectedItems);
    }

    [Fact]
    public void Remove_未选中条目_不触发事件()
    {
        _service.SelectionChanged += CountChanges;

        _service.Remove(_items[2]);

        Assert.Equal(0, ChangedCount);
    }

    [Fact]
    public void SelectedItems_按集合顺序返回而非登记顺序()
    {
        _service.Toggle(_items[3]);
        _service.Toggle(_items[1]);

        var selected = _service.SelectedItems;

        Assert.Equal(2, selected.Count);
        Assert.Same(_items[1], selected[0]);
        Assert.Same(_items[3], selected[1]);
    }
}
