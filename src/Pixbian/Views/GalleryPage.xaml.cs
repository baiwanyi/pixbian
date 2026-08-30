/**
 * 图库页代码后置（M2）。
 * 职责：把界面事件转交 ViewModel，管理网格 / 自适应两种视图的切换与工具栏交互，
 *      并在条目容器进入视口时触发缩略图按需加载。
 * 复用约定：所有数据操作一律委托 ViewModel，本页面不写查询、不碰数据库；
 *          视图与缩略图尺寸变更统一经 ShellViewModel.SaveSettingsAsync 持久化并广播，
 *          再由 MainWindow.ApplySettings 回流应用，本页面不直接写设置。
 * 关键约束：两种视图都不能让自定义 ItemsPanel 直接承载分组数据——分组 ListViewBase 的
 *          ItemsPanel 只能拿到 GroupItem 组容器，条目不会渲染；故自适应视图用 ItemsControl
 *          按组迭代、每组内嵌一个非分组 GridView（内层禁用滚动，由外层 ScrollViewer 统一滚动，
 *          JustifiedPanel 不做 UI 虚拟化，条目规模由分页增量加载控制），
 *          网格视图直接用条目集合的非分组 GridView + 内建 ItemsWrapGrid。
 *          ContainerContentChanging 是虚拟化列表唯一的「进入视口」时机，
 *          必须在此触发按需加载，该事件是同步的，不 await 加载结果。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pixbian.Controls;
using Pixbian.Core.Models;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>图库页。</summary>
public sealed partial class GalleryPage : Page, INotifyPropertyChanged
{
    private const int GridItemPadding = 8;

    private readonly List<(GridView Grid, JustifiedPanel Panel)> _justifiedGrids = [];

    private bool _isGridView;
    private bool _isJustifiedView = true;
    private bool _isSelectionMode;
    private bool _hasSelection;
    private bool _hasItems;
    private string _selectionCountText = "已选择 0 个项目";
    private bool _isEmpty = true;

    /// <summary>初始化图库页。</summary>
    /// <param name="viewModel">图库视图模型，由依赖注入提供。</param>
    /// <param name="shell">应用外壳视图模型，用于持久化视图与缩略图尺寸设置。</param>
    public GalleryPage(GalleryViewModel viewModel, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(shell);

        ViewModel = viewModel;
        _shell = shell;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 网格面板的尺寸绑定使用传统 Binding，需要显式提供 DataContext。
        DataContext = this;

        InitializeComponent();

        // F5 快捷键从头开始幻灯片播放。
        KeyDown += OnGalleryPageKeyDown;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>图库视图模型。</summary>
    public GalleryViewModel ViewModel { get; }

    private readonly ShellViewModel _shell;

    /// <summary>当前选中项集合，供批量操作使用。</summary>
    public IReadOnlyList<MediaItemViewModel> Selection { get; private set; } = [];

    /// <summary>承载本页的主窗口，用于打开图片查看器。</summary>
    public MainWindow Owner { get; set; } = null!;

    /// <summary>是否存在选中项。</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetField(ref _hasSelection, value);
    }

    /// <summary>集合是否非空，控制幻灯片按钮可用性。</summary>
    public bool HasItems
    {
        get => _hasItems;
        private set => SetField(ref _hasItems, value);
    }

    /// <summary>集合是否为空。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetField(ref _isEmpty, value);
    }

    /// <summary>是否显示分组网格视图。</summary>
    public bool IsGridView
    {
        get => _isGridView;
        private set => SetField(ref _isGridView, value);
    }

    /// <summary>是否处于勾选式选择模式；决定是否在条目上显示选择复选框与遮罩。</summary>
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        private set => SetField(ref _isSelectionMode, value);
    }

    /// <summary>是否显示自适应行式视图。</summary>
    public bool IsJustifiedView
    {
        get => _isJustifiedView;
        private set => SetField(ref _isJustifiedView, value);
    }

    /// <summary>选择模式下顶部栏显示的选中计数文本（如「已选择 3 个项目」）。</summary>
    public string SelectionCountText
    {
        get => _selectionCountText;
        private set => SetField(ref _selectionCountText, value);
    }

    /// <summary>网格项宽度，由缩略图档位加内边距推导。</summary>
    public double GridItemWidth => ViewModel.ThumbnailSize + GridItemPadding;

    /// <summary>网格项高度，与宽度一致以保持正方形。</summary>
    public double GridItemHeight => GridItemWidth;

    /// <summary>应用视图模式，切换两种视图的可见性并同步菜单勾选状态。</summary>
    /// <param name="viewMode">目标视图模式。</param>
    public void ApplyViewMode(GalleryViewMode viewMode)
    {
        IsGridView = viewMode == GalleryViewMode.Grid;
        IsJustifiedView = viewMode == GalleryViewMode.Justified;

        // 程序设置 IsChecked 不会触发 Click 事件，无递归风险。
        if (LayoutJustifiedItem is not null)
        {
            LayoutJustifiedItem.IsChecked = viewMode == GalleryViewMode.Justified;
            LayoutGridItem.IsChecked = viewMode == GalleryViewMode.Grid;
        }
    }

    /// <summary>应用缩略图尺寸变化，刷新网格项尺寸、自适应行高与菜单勾选状态。</summary>
    public void ApplyThumbnailSize()
    {
        OnPropertyChanged(nameof(GridItemWidth));
        OnPropertyChanged(nameof(GridItemHeight));

        foreach (var (_, panel) in _justifiedGrids)
        {
            panel.RowHeight = ViewModel.ThumbnailSize;
        }

        SyncSizeChecks();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.ItemCount))
        {
            IsEmpty = ViewModel.ItemCount == 0;
            HasItems = ViewModel.ItemCount > 0;
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not MediaItemViewModel item)
        {
            return;
        }

        // 不 await：虚拟化管线要求该事件同步返回，等待 IO 会阻塞滚动。
        // 网格视图的内容区是正方形，Uniform 不会超出它，显示区最长边恒为格子边长，
        // 故直接回写正方形尺寸，避免按宽高比放大请求造成的无用过采样。
        item.SetDisplaySize(ViewModel.ThumbnailSize, ViewModel.ThumbnailSize);
        _ = item.EnsureThumbnailAsync(ViewModel.ThumbnailSize);
    }

    /// <summary>聚合当前视图的选中项：网格视图取主控件，自适应视图汇总各分组控件。</summary>
    private List<MediaItemViewModel> CollectSelection()
    {
        if (IsGridView)
        {
            return GridViewControl.SelectedItems.OfType<MediaItemViewModel>().ToList();
        }

        return [.. _justifiedGrids.SelectMany(g => g.Grid.SelectedItems.OfType<MediaItemViewModel>())];
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Selection = CollectSelection();
        HasSelection = Selection.Count > 0;
        UpdateSelectionCount();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        // 勾选式选择模式下，单击仅用于切换选中态，由 GridView 多选机制处理，不记录为当前项。
        if (IsSelectionMode)
        {
            return;
        }

        if (e.ClickedItem is MediaItemViewModel item)
        {
            ViewModel.SelectedItem = item;
        }
    }

    /// <summary>双击条目时在查看器中打开。</summary>
    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindItemContainer(e.OriginalSource as DependencyObject)?.Content is MediaItemViewModel item)
        {
            await Owner.OpenViewerAsync(item);
        }
    }

    private async void OnAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SetFavoriteForSelectionAsync(Selection);
    }

    private async void OnRemoveFromIndexClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RemoveFromIndexAsync(Selection);
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ReloadCommand.ExecuteAsync(null);
    }

    /// <summary>点击「选择」按钮：进入勾选式选择模式，所有列表切换为多选并清空既有选择。</summary>
    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        IsSelectionMode = true;

        GridViewControl.SelectionMode = ListViewSelectionMode.Multiple;
        GridViewControl.SelectedItems.Clear();

        foreach (var (grid, _) in _justifiedGrids)
        {
            grid.SelectionMode = ListViewSelectionMode.Multiple;
            grid.SelectedItems.Clear();
        }

        UpdateSelectionCount();
    }

    /// <summary>点击「取消」：退出选择模式并清空选择。</summary>
    private void OnCancelSelectionClick(object sender, RoutedEventArgs e)
    {
        IsSelectionMode = false;

        GridViewControl.SelectionMode = ListViewSelectionMode.Extended;
        GridViewControl.SelectedItems.Clear();

        foreach (var (grid, _) in _justifiedGrids)
        {
            grid.SelectionMode = ListViewSelectionMode.Extended;
            grid.SelectedItems.Clear();
        }

        Selection = [];
        HasSelection = false;
        UpdateSelectionCount();
    }

    /// <summary>全选当前列表所有条目。</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        SelectAll(true);
    }

    /// <summary>取消全选。</summary>
    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        SelectAll(false);
    }

    /// <summary>批量切换所有列表的选中态。</summary>
    /// <param name="select">true 表示全选，false 表示清空。</param>
    private void SelectAll(bool select)
    {
        ToggleAllGrid(GridViewControl, select);

        foreach (var (grid, _) in _justifiedGrids)
        {
            ToggleAllGrid(grid, select);
        }

        UpdateSelectionCount();
    }

    private static void ToggleAllGrid(GridView grid, bool select)
    {
        var items = grid.Items.Cast<object>().ToList();

        if (select)
        {
            foreach (var item in items)
            {
                grid.SelectedItems.Add(item);
            }
        }
        else
        {
            grid.SelectedItems.Clear();
        }
    }

    /// <summary>刷新顶部选中计数文本。</summary>
    private void UpdateSelectionCount()
    {
        var count = CollectSelection().Count;
        SelectionCountText = count == 0 ? "未选择任何项目" : $"已选择 {count} 个项目";
    }

    /// <summary>键盘快捷键：F5 从头开始幻灯片播放。</summary>
    private void OnGalleryPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F5 && !e.Handled)
        {
            e.Handled = true;
            OnSlideShowClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>滚动接近底部时加载下一页；两视图共用。</summary>
    private async void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 拖动过程中的中间态不触发，避免滚动时连续发起请求。
        if (e.IsIntermediate || sender is not ScrollViewer viewer)
        {
            return;
        }

        // 距底部两屏内即预取，避免用户滚到底后看到空白。
        var remaining = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;
        if (remaining > viewer.ViewportHeight * 2)
        {
            return;
        }

        await ViewModel.LoadMoreCommand.ExecuteAsync(null);
    }

    /// <summary>网格视图加载后订阅其内部滚动条，用于触底加载下一页。</summary>
    private void OnGridViewControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not GridView grid || FindDescendant<ScrollViewer>(grid) is not { } viewer)
        {
            return;
        }

        // 先解除再订阅，避免 Loaded 重复触发导致重复订阅。
        viewer.ViewChanged -= OnScrollViewChanged;
        viewer.ViewChanged += OnScrollViewChanged;
    }

    /// <summary>自适应视图分组控件加载：登记实例并同步行高与选择模式。</summary>
    private void OnJustifiedGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not GridView grid)
        {
            return;
        }

        grid.SelectionMode = IsSelectionMode
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.Extended;

        if (FindDescendant<JustifiedPanel>(grid) is not { } panel)
        {
            return;
        }

        panel.RowHeight = ViewModel.ThumbnailSize;
        _justifiedGrids.Add((grid, panel));
    }

    /// <summary>自适应视图分组控件卸载：解除登记，避免聚合到失效实例的选择。</summary>
    private void OnJustifiedGridUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is GridView grid)
        {
            _justifiedGrids.RemoveAll(g => g.Grid == grid);
        }
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

    /// <summary>在视觉树中深度优先查找指定类型的后代。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            var result = FindDescendant<T>(child);

            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>从选中项（或第一张图片）开始幻灯片播放。</summary>
    private async void OnSlideShowClick(object sender, RoutedEventArgs e)
    {
        var start = Selection.FirstOrDefault(i => !i.IsVideo)
            ?? ViewModel.Items.FirstOrDefault(i => !i.IsVideo);

        if (start is null)
        {
            return;
        }

        await Owner.OpenViewerAsync(start, startSlideShow: true);
    }

    private async void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            && Enum.TryParse<MediaSortOrder>(tag, out var order))
        {
            await ViewModel.ApplySortOrderAsync(order);
        }
    }

    private async void OnKindFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { IsChecked: true, Tag: string tag })
        {
            return;
        }

        var kind = tag switch
        {
            "Image" => MediaKind.Image,
            "Video" => MediaKind.Video,
            _ => (MediaKind?)null
        };

        await ViewModel.ApplyKindFilterAsync(kind);
    }

    /// <summary>布局菜单点击：经外壳持久化视图模式并广播应用，值未变化时跳过。</summary>
    private async void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            && Enum.TryParse(tag, out GalleryViewMode mode)
            && mode != _shell.Settings.ViewMode)
        {
            await _shell.SaveSettingsAsync(_shell.Settings with { ViewMode = mode });
        }
    }

    /// <summary>尺寸菜单点击：经外壳持久化缩略图尺寸并广播应用，值未变化时跳过。</summary>
    private async void OnSizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            && int.TryParse(tag, out var size)
            && size != _shell.Settings.ThumbnailSize)
        {
            await _shell.SaveSettingsAsync(_shell.Settings with { ThumbnailSize = size });
        }
    }

    /// <summary>按当前生效的缩略图尺寸同步大小组菜单的勾选状态。</summary>
    private void SyncSizeChecks()
    {
        if (SizeSmallItem is null)
        {
            return;
        }

        // 直接与各菜单项的 Tag 比对：Tag 即该档位要写入设置的值，二者同源才不会出现
        // 「菜单点得动、勾选却对不上」的错位（改动 Presets 时只需同步 Tag）。
        var size = ViewModel.ThumbnailSize;
        SizeSmallItem.IsChecked = size == ParseTag(SizeSmallItem);
        SizeMediumItem.IsChecked = size == ParseTag(SizeMediumItem);
        SizeLargeItem.IsChecked = size == ParseTag(SizeLargeItem);
    }

    /// <summary>读取菜单项 Tag 中的缩略图档位值。</summary>
    /// <param name="item">尺寸菜单项，Tag 须为整数字符串。</param>
    private static int ParseTag(RadioMenuFlyoutItem item) =>
        int.TryParse(item.Tag as string, out var value) ? value : ThumbnailSizes.Default;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }
}
