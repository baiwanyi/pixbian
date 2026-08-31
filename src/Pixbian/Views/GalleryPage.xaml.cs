/**
 * 图库页代码后置（M2）。
 * 职责：把界面事件转交 ViewModel，管理网格 / 自适应两种视图的切换与工具栏交互，
 *      并在条目容器进入视口时触发缩略图按需加载。
 * 复用约定：所有数据操作一律委托 ViewModel，本页面不写查询、不碰数据库；
 *          视图与缩略图尺寸变更统一经 ShellViewModel.SaveSettingsAsync 持久化并广播，
 *          再由 MainWindow.ApplySettings 回流应用，本页面不直接写设置。
 *          工具栏溢出用 AdaptiveTrigger + VisualState 声明（规范 §2.3【必须】），不监听 SizeChanged；
 *          被收起的命令由「更多」菜单按各按钮当前 Visibility 补齐，带下拉菜单的按钮以同名子菜单提供，
 *          菜单内容每次 Opening 时按当前状态重建，故无需维护菜单项引用去反向同步勾选。
 * 关键约束：两种视图都用条目集合的非分组 GridView（内层禁用滚动，由外层 ScrollViewer 统一滚动，
 *          JustifiedPanel 不做 UI 虚拟化，条目规模由分页增量加载控制），网格视图用内建 ItemsWrapGrid。
 *          ContainerContentChanging 是虚拟化列表唯一的「进入视口」时机，
 *          必须在此触发按需加载，该事件是同步的，不 await 加载结果。
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pixbian.Controls;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Pixbian.Views;

/// <summary>图库页。</summary>
public sealed partial class GalleryPage : Page, INotifyPropertyChanged
{
    private const int GridItemPadding = 8;

    private const string SortKeyGroupName = "SortKey";
    private const string SortDirectionGroupName = "SortDirection";
    private const string KindGroupName = "Kind";
    private const string LayoutGroupName = "Layout";
    private const string SizeGroupName = "Size";

    private const string SortGlyph = "\uE174";
    private const string FilterGlyph = "\uE71C";
    private const string ViewModeGlyph = "\uECA5";
    private const string SlideShowGlyph = "\uE786";
    private const string SelectGlyph = "\uE73A";
    private const string SelectAllGlyph = "\uE8B3";
    private const string ClearGlyph = "\uE711";

    /// <summary>排序依据项：键、文本、图标。菜单每次打开都据此重建。</summary>
    private static readonly (MediaSortKey Key, string Text, string Glyph)[] SortKeyItems =
    [
        (MediaSortKey.Random, "随机", "\uE8B1"),
        (MediaSortKey.ModifiedDate, "日期", "\uE787"),
        (MediaSortKey.FileSize, "大小", "\uE8A5"),
        (MediaSortKey.FileName, "名字", "\uE8C1")
    ];

    /// <summary>排序方向项：方向、文本、图标。</summary>
    private static readonly (SortDirection Direction, string Text, string Glyph)[] SortDirectionItems =
    [
        (SortDirection.Ascending, "升序", "\uE74A"),
        (SortDirection.Descending, "降序", "\uE74B")
    ];

    /// <summary>类型筛选项：类型（null 为全部）、文本、图标。</summary>
    private static readonly (MediaKind? Kind, string Text, string Glyph)[] KindItems =
    [
        (null, "所有媒体", "\uE8A9"),
        (MediaKind.Image, "照片", "\uE91B"),
        (MediaKind.Video, "视频", "\uE8B2")
    ];

    private static readonly (GalleryViewMode Mode, string Text, string Glyph)[] LayoutItems =
    [
        (GalleryViewMode.Justified, "等高", "\uECA5"),
        (GalleryViewMode.Grid, "方形", "\uF0E2")
    ];

    /// <summary>尺寸档位的文本与图标，按 ThumbnailSizes.Presets 顺序配对（Zip 以短者为准）。</summary>
    private static readonly (string Text, string Glyph)[] SizeLabels =
    [
        ("小", "\uF232"),
        ("中", "\uF57C"),
        ("大", "\uE71A")
    ];

    /// <summary>尺寸档位项：档位值取自 ThumbnailSizes.Presets，保证与 Normalize 的取值域一致。</summary>
    private static IEnumerable<(int Size, string Text, string Glyph)> SizeItems =>
        ThumbnailSizes.Presets.Zip(SizeLabels, (size, label) => (size, label.Text, label.Glyph));

    private readonly List<(GridView Grid, JustifiedPanel Panel)> _justifiedGrids = [];

    // 右键菜单命中的目标项；菜单内各操作据此执行，避免依赖可能过期的 SelectedItem。
    private MediaItemViewModel? _contextItem;

    private bool _isGridView;
    private bool _isJustifiedView = true;
    private bool _isSelectionMode;
    private bool _hasSelection;
    private MediaSortKey _sortKey = MediaSortKey.ModifiedDate;
    private SortDirection _sortDirection = SortDirection.Descending;
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

    /// <summary>应用视图模式，切换两种视图的可见性。</summary>
    /// <remarks>菜单勾选不在此同步：下拉菜单每次 Opening 时按当前设置重建，勾选态天然最新。</remarks>
    /// <param name="viewMode">目标视图模式。</param>
    public void ApplyViewMode(GalleryViewMode viewMode)
    {
        IsGridView = viewMode == GalleryViewMode.Grid;
        IsJustifiedView = viewMode == GalleryViewMode.Justified;
    }

    /// <summary>应用缩略图尺寸变化，刷新网格项尺寸与自适应行高。</summary>
    public void ApplyThumbnailSize()
    {
        OnPropertyChanged(nameof(GridItemWidth));
        OnPropertyChanged(nameof(GridItemHeight));

        foreach (var (_, panel) in _justifiedGrids)
        {
            panel.RowHeight = ViewModel.ThumbnailSize;
        }
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
        var size = ViewModel.ThumbnailSize;

        if (sender == GridViewControl)
        {
            // 网格视图的内容区是正方形，Uniform 不会超出它，显示区最长边恒为格子边长。
            item.SetDisplaySize(size, size);
        }
        else
        {
            // 自适应视图的条目宽 = 行高 × 宽高比。必须按此估算回写，
            // 否则首帧按正方形请求、面板回写真实尺寸后必然触发一次升级加载，
            // 升级完成时 ImageBrush 换源，旧纹理被清除而新纹理尚未就绪，图片会闪一帧。
            // 估算规则与 ResolveDecodeSize 一致（宽高比钳制到 [1, 2]，防全景图解码尺寸失控）。
            var estimatedWidth = size * Math.Clamp(item.AspectRatio, 1.0, 2.0);
            item.SetDisplaySize(estimatedWidth, size);
        }

        _ = item.EnsureThumbnailAsync(size);
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
        // 阻止事件继续冒泡，避免外层容器（如自适应视图的 ScrollViewer）再次触发本处理程序。
        e.Handled = true;

        var container = FindItemContainer(e.OriginalSource as DependencyObject);
        var item = container?.Content as MediaItemViewModel;

        Diagnostics.Log($"{DateTime.Now:HH:mm:ss.fff}|DBLTAP|sender={sender?.GetType().Name}|container={container?.GetType().Name}|item={item?.FileName ?? "null"}");

        if (item is not null)
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

    /// <summary>快捷键 Ctrl+A：全选。</summary>
    /// <remarks>
    /// 非选择模式下条目不显示复选框与遮罩，直接全选不会有任何视觉反馈，故先切到选择模式
    /// （OnSelectClick 会清空既有选择，随后再全选，顺序不可颠倒）。
    /// </remarks>
    private void OnSelectAllAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (!IsSelectionMode)
        {
            OnSelectClick(this, new RoutedEventArgs());
        }

        SelectAll(true);
    }

    /// <summary>快捷键 Ctrl+D / Esc：清空全部选择（停留在选择模式）。</summary>
    private void OnSelectNoneAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
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

    /// <summary>键盘快捷键：F5 从头开始幻灯片播放；Ctrl+C 复制文件；Delete 移入回收站。</summary>
    private void OnGalleryPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F5 && !e.Handled)
        {
            e.Handled = true;
            OnSlideShowClick(this, new RoutedEventArgs());
            return;
        }

        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                is CoreVirtualKeyStates.Down or CoreVirtualKeyStates.Locked)
        {
            if (e.Key == VirtualKey.C && !e.Handled)
            {
                e.Handled = true;
                _ = CopyItemToClipboardAsync(GetContextTarget());
            }

            return;
        }

        if (e.Key == VirtualKey.Delete && !e.Handled)
        {
            e.Handled = true;
            _ = DeleteContextItemsAsync(GetContextTarget());
            return;
        }

        if ((e.Key == VirtualKey.F2 || e.Key == VirtualKey.F3) && !e.Handled)
        {
            var target = GetContextTarget();
            var first = target.Count > 0 ? target[0] : null;
            if (first is null)
            {
                return;
            }

            e.Handled = true;
            if (e.Key == VirtualKey.F2)
            {
                _ = ShowRenameDialogAsync(first);
            }
            else
            {
                OpenInFileExplorer(first.Item.Path);
            }
        }
    }

    /// <summary>取得右键/快捷键操作的目标集合：选择模式下为整组选择，否则为单选当前项。</summary>
    private IReadOnlyList<MediaItemViewModel> GetContextTarget()
    {
        if (IsSelectionMode)
        {
            return Selection;
        }

        return ViewModel.SelectedItem is { } single ? new[] { single } : [];
    }

    /// <summary>滚动接近底部时加载下一页；两视图共用。</summary>
    private async void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 拖动过程中的中间态不触发，避免滚动时连续发起请求。
        if (e.IsIntermediate || sender is not ScrollViewer viewer)
        {
            return;
        }

        // 滚动停止即取消已滚出视口且仍在解码途中的条目，把信号量槽位让给即将进入视口的新条目。
        CancelOffscreenThumbnails();

        // 距底部两屏内即预取，避免用户滚到底后看到空白。
        var remaining = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;
        if (remaining > viewer.ViewportHeight * 2)
        {
            return;
        }

        await ViewModel.LoadMoreCommand.ExecuteAsync(null);
    }

    /// <summary>取消所有已滚出视口且仍在解码途中的缩略图加载。</summary>
    /// <remarks>
    /// 虚拟化列表的 ContainerFromItem 对可见容器返回非 null、对回收容器返回 null，
    /// 据此判定可见性；已在途的解码任务会被 EnsureThumbnailAsync 的取消令牌中断，
    /// 槽位立即释放给新进入视口的条目。已加载完成的条目（Thumbnail 非 null）不受影响。
    /// </remarks>
    private void CancelOffscreenThumbnails()
    {
        var grids = new List<GridView> { GridViewControl };

        grids.AddRange(_justifiedGrids.Select(g => g.Grid));

        foreach (var item in ViewModel.Items)
        {
            var visible = false;

            foreach (var grid in grids)
            {
                if (grid.ContainerFromItem(item) is not null)
                {
                    visible = true;
                    break;
                }
            }

            if (!visible)
            {
                item.CancelPendingLoad();
            }
        }
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

    /// <summary>在图片上右键：定位命中项，弹出上下文菜单并填充只读信息项。</summary>
    /// <remarks>
    /// 右键命中项作为本次菜单的操作目标，统一经 _contextItem 传递，避免依赖可能过期的 SelectedItem；
    /// 菜单结构见 ItemContextMenu.xaml，其 Click 在此按 x:Name 绑定（资源字典不带 x:Class）。
    /// </remarks>
    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var container = FindItemContainer(e.OriginalSource as DependencyObject);
        var item = container?.Content as MediaItemViewModel;

        if (item is null)
        {
            return;
        }

        // 右键命中项即本次操作目标；同步为当前项，使工具栏状态与菜单一致。
        ViewModel.SelectedItem = item;
        _contextItem = item;

        // 每次弹出都构建全新的 MenuFlyout 实例：Application.Current.Resources 取出的是共享单例，
        // 首次 ShowAt 后其 FlyoutPresenter 残留在旧视觉树/XamlRoot 上，再次 ShowAt 会因新旧
        // XamlRoot 冲突而抛 E_INVALIDARG("参数错误")。每次新建实例可彻底规避该异常。
        var flyout = CreateItemContextMenu();

        flyout.XamlRoot ??= this.XamlRoot;

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuOpen" }) is MenuFlyoutItem open)
        {
            open.Click += OnOpenClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuCopy" }) is MenuFlyoutItem copy)
        {
            copy.Click += OnCopyClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuCopyPath" }) is MenuFlyoutItem copyPath)
        {
            copyPath.Click += OnCopyPathClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuRename" }) is MenuFlyoutItem rename)
        {
            rename.Click += OnRenameClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuReveal" }) is MenuFlyoutItem reveal)
        {
            reveal.Click += OnRevealClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuDelete" }) is MenuFlyoutItem delete)
        {
            delete.Click += OnDeleteClick;
        }

        FillMenuInfo(flyout, item);

        // 每次弹出前解绑，避免重复订阅导致 Click 多次触发。
        flyout.Opened += OnContextMenuOpened;
        flyout.Closed += OnContextMenuClosed;

        // 跟随鼠标位置弹出：以页面根为锚点，偏移取右键指针相对页面根的坐标，
        // 菜单即出现在光标处，而非钉在图片条目容器边缘（固定位置）。
        var pointerPos = e.GetPosition(this);
        flyout.ShowAt(this, pointerPos);
    }

    /// <summary>构建图片右键上下文菜单的全新实例。</summary>
    /// <remarks>
    /// 每次调用返回独立 MenuFlyout，菜单项按 Name 暴露以便按 x:Name 绑定 Click 事件；
    /// 结构集中于此，新增/调整菜单项只需改此方法（对应原 ItemContextMenu.xaml）。
    /// 只读信息项（大小/尺寸/日期）用 IsEnabled="False" 呈现，前景色走主题键、不加图标，
    /// 并套用应用级资源 ReadOnlyMenuItemStyle（仅约束 MaxWidth）。
    /// 删除项前景色固定 IndianRed，并通过项级主题键覆盖 PointerOver/Pressed 视觉状态保持红色。
    /// 可点击项带 SymbolIcon 图标；重命名绑定 F2、在资源管理器中打开绑定 F3（亦见 OnGalleryPageKeyDown）。
    /// </remarks>
    private static MenuFlyout CreateItemContextMenu()
    {
        // 只读信息项前景色统一引用主题键，明暗主题自动切换（对应原 XAML 的 ContextMenuInfoForeground）。
        var infoBrush = Application.Current.Resources["ContextMenuInfoForeground"] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

        // 只读项样式经由 XAML 编译期应用级资源提供（见 App.xaml 的 ReadOnlyMenuItemStyle），
        // 不在此用 XamlReader.Load 动态解析，规避运行时应用该类模板造成的旋转忙碌光标。
        var readOnlyStyle = Application.Current.Resources["ReadOnlyMenuItemStyle"] as Style;

        var flyout = new MenuFlyout
        {
            Items =
            {
                new MenuFlyoutItem { Name = "MenuOpen", Text = "打开", Icon = new SymbolIcon(Symbol.View) },
                new MenuFlyoutItem { Name = "MenuCopy", Text = "复制", Icon = new SymbolIcon(Symbol.Copy), KeyboardAcceleratorTextOverride = "Ctrl+C" },
                new MenuFlyoutItem { Name = "MenuCopyPath", Text = "复制为路径", Icon = new SymbolIcon(Symbol.Link) },
                new MenuFlyoutItem { Name = "MenuRename", Text = "重命名", Icon = new SymbolIcon(Symbol.Rename), KeyboardAcceleratorTextOverride = "F2" },
                new MenuFlyoutItem { Name = "MenuReveal", Text = "在文件资源管理器中打开", Icon = new SymbolIcon(Symbol.OpenLocal), KeyboardAcceleratorTextOverride = "F3" },
                new MenuFlyoutSeparator(),
                new MenuFlyoutItem { Name = "MenuSize", Text = "大小：", IsEnabled = false, Foreground = infoBrush, Style = readOnlyStyle },
                new MenuFlyoutItem { Name = "MenuDimensions", Text = "尺寸：", IsEnabled = false, Foreground = infoBrush, Style = readOnlyStyle },
                new MenuFlyoutItem { Name = "MenuDate", Text = "日期：", IsEnabled = false, Foreground = infoBrush, Style = readOnlyStyle },
                new MenuFlyoutSeparator(),
            },
        };

        // 删除项：前景固定 IndianRed，且 hover/pressed 视觉状态（默认模板会把 TextBlock.Foreground
        // 改回主题键 MenuFlyoutItemForegroundPointerOver/Pressed）通过项级主题键覆盖保持红色，
        // 不重写 ControlTemplate（避免触发旋转忙碌光标）。
        var deleteBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        var deleteItem = new MenuFlyoutItem
        {
            Name = "MenuDelete",
            Text = "删除",
            Icon = new SymbolIcon(Symbol.Delete),
            KeyboardAcceleratorTextOverride = "Delete",
            Foreground = deleteBrush,
        };
        deleteItem.Resources["MenuFlyoutItemForegroundPointerOver"] = deleteBrush;
        deleteItem.Resources["MenuFlyoutItemForegroundPressed"] = deleteBrush;
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    /// <summary>菜单打开时填充只读信息项（大小/尺寸/日期/位置）。</summary>
    private static void FillMenuInfo(MenuFlyout flyout, MediaItemViewModel item)
    {
        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuSize" }) is MenuFlyoutItem size)
        {
            size.Text = $"大小：{item.FileSizeText}";
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuDimensions" }) is MenuFlyoutItem dims)
        {
            dims.Text = $"尺寸：{item.DimensionText}";
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuDate" }) is MenuFlyoutItem date)
        {
            var taken = item.Item.TakenUtc ?? item.Item.ModifiedUtc;
            var text = item.Item.TakenUtc is null
                ? "未知"
                : taken.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            date.Text = $"日期：{text}";
        }
    }

    private void OnContextMenuOpened(object? sender, object e)
    {
        // 占位：菜单已通过 FillMenuInfo 预填，无需重复处理。
    }

    private void OnContextMenuClosed(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Opened -= OnContextMenuOpened;
        flyout.Closed -= OnContextMenuClosed;

        foreach (var child in flyout.Items)
        {
            if (child is MenuFlyoutItem item)
            {
                item.Click -= OnOpenClick;
                item.Click -= OnCopyClick;
                item.Click -= OnCopyPathClick;
                item.Click -= OnRenameClick;
                item.Click -= OnRevealClick;
                item.Click -= OnDeleteClick;
            }
        }
    }

    /// <summary>菜单「打开」：复用查看器打开命中项。</summary>
    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            await Owner.OpenViewerAsync(item);
        }
    }

    /// <summary>菜单「复制」：把文件作为 StorageItem 写入剪贴板。</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        _ = CopyItemToClipboardAsync(GetContextTarget());
    }

    /// <summary>菜单「复制为路径」：把文件完整路径写入剪贴板文本。</summary>
    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(item.Item.Path);
            Clipboard.SetContent(package);
        }
    }

    /// <summary>菜单「重命名」：弹出输入框，确认后委托 ViewModel 改文件并同步索引。</summary>
    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            await ShowRenameDialogAsync(item);
        }
    }

    /// <summary>菜单「删除」：把命中项移入回收站。</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        _ = DeleteContextItemsAsync(GetContextTarget());
    }

    /// <summary>菜单「在文件资源管理器中打开」：选中命中文件并定位到其所在文件夹。</summary>
    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            OpenInFileExplorer(item.Item.Path);
        }
    }

    /// <summary>用资源管理器打开并选中指定文件（explorer.exe /select 形式）。</summary>
    /// <remarks>路径来自受信任的索引数据，经 argv 形式传入，杜绝命令注入；外层用引号包裹路径，
    /// 应对含空格的路径。unpackaged 下 explorer.exe 由系统 PATH 解析，无需硬编码绝对路径。</remarks>
    private static void OpenInFileExplorer(string filePath)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>把目标集合首个文件复制到剪贴板（StorageItem 方式，支持跨应用粘贴）。</summary>
    private static async Task CopyItemToClipboardAsync(IReadOnlyList<MediaItemViewModel> targets)
    {
        var first = targets.Count > 0 ? targets[0] : null;

        if (first is null)
        {
            return;
        }

        var file = await StorageFile.GetFileFromPathAsync(first.Item.Path);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetStorageItems(new[] { file });
        Clipboard.SetContent(package);
    }

    /// <summary>弹出重命名对话框，确认后调用 ViewModel 重命名并回写路径。</summary>
    private async Task ShowRenameDialogAsync(MediaItemViewModel item)
    {
        var input = new TextBox
        {
            Text = item.FileName,
        };

        // WinUI 3 的 TextBox 无 SelectAllOnFocus 属性，改为获得焦点时全选，便于用户直接覆盖输入。
        input.Loaded += (_, _) => input.SelectAll();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = input,
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            // 对话框被外部关闭（如应用退出）时 ShowAsync 会取消，属预期行为，不应视为崩溃。
            return;
        }

        if (result is not ContentDialogResult.Primary)
        {
            return;
        }

        var newName = input.Text.Trim();

        if (string.IsNullOrWhiteSpace(newName) || newName == item.FileName)
        {
            return;
        }

        var error = await ViewModel.RenameAsync(item, newName);

        if (!string.IsNullOrEmpty(error))
        {
            await ShowErrorAsync("重命名失败", error);
        }
    }

    /// <summary>把目标集合移入回收站，并同步从索引与视图集合中移除。</summary>
    private async Task DeleteContextItemsAsync(IReadOnlyList<MediaItemViewModel> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var (succeeded, error) = await ViewModel.DeleteFilesAsync(targets);

        if (!succeeded && !string.IsNullOrEmpty(error))
        {
            await ShowErrorAsync("删除失败", error);
        }
    }

    /// <summary>弹出错误提示对话框。</summary>
    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
            Content = message,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            // 对话框被外部关闭（如应用退出）时 ShowAsync 会取消，属预期行为，不应视为崩溃。
        }
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

    /// <summary>排序依据菜单点击：沿用当前方向重新加载。</summary>
    private async void OnSortKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            || !Enum.TryParse<MediaSortKey>(tag, out var key))
        {
            return;
        }

        _sortKey = key;

        await ViewModel.ApplySortOrderAsync(key, _sortDirection);
    }

    /// <summary>升降序菜单点击：沿用当前排序依据重新加载。</summary>
    private async void OnSortDirectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            || !Enum.TryParse<SortDirection>(tag, out var direction))
        {
            return;
        }

        _sortDirection = direction;

        await ViewModel.ApplySortOrderAsync(_sortKey, direction);
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

    /// <summary>构建排序菜单项：依据 + 分隔线 + 方向。</summary>
    /// <remarks>随机排序无方向语义，两项置灰，以免产生无效果的重新加载。</remarks>
    private void BuildSortItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (key, text, glyph) in SortKeyItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = SortKeyGroupName,
                IsChecked = key == _sortKey,
                Tag = key.ToString(),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnSortKeyClick;
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());

        var hasDirection = _sortKey != MediaSortKey.Random;

        foreach (var (direction, text, glyph) in SortDirectionItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = SortDirectionGroupName,
                IsChecked = direction == _sortDirection,
                IsEnabled = hasDirection,
                Tag = direction.ToString(),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnSortDirectionClick;
            items.Add(item);
        }
    }

    /// <summary>构建类型筛选菜单项。</summary>
    private void BuildKindItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (kind, text, glyph) in KindItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = KindGroupName,
                IsChecked = kind == ViewModel.KindFilter,
                Tag = kind?.ToString() ?? "All",
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnKindFilterClick;
            items.Add(item);
        }
    }

    /// <summary>构建显示与大小菜单项：布局 + 分隔线 + 尺寸档位。</summary>
    private void BuildViewItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (mode, text, glyph) in LayoutItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = LayoutGroupName,
                IsChecked = mode == _shell.Settings.ViewMode,
                Tag = mode.ToString(),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnLayoutClick;
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());

        foreach (var (size, text, glyph) in SizeItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = SizeGroupName,
                IsChecked = size == _shell.Settings.ThumbnailSize,
                Tag = size.ToString(CultureInfo.InvariantCulture),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnSizeClick;
            items.Add(item);
        }
    }

    private void OnSortMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            RebuildMenu(flyout, BuildSortItems);
        }
    }

    private void OnKindMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            RebuildMenu(flyout, BuildKindItems);
        }
    }

    private void OnViewMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            RebuildMenu(flyout, BuildViewItems);
        }
    }

    /// <summary>「更多」菜单打开时重建：先放被收起的命令，再放全选 / 取消选择。</summary>
    private void OnMoreMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Items.Clear();

        if (SelectButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateMenuItem("选择", SelectGlyph, OnSelectClick));
        }

        if (SlideShowButton.Visibility == Visibility.Collapsed)
        {
            var play = CreateMenuItem("幻灯片放映", SlideShowGlyph, OnSlideShowClick);
            play.IsEnabled = HasItems;
            flyout.Items.Add(play);
        }

        // 带下拉菜单的按钮收起后，以同名子菜单提供，内容与按钮上的菜单完全同源。
        if (SortButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateSubMenu("排序", SortGlyph, BuildSortItems));
        }

        if (FilterButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateSubMenu("筛选", FilterGlyph, BuildKindItems));
        }

        if (ViewModeButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateSubMenu("显示和大小", ViewModeGlyph, BuildViewItems));
        }

        // 宽度足够时没有收起任何按钮，分隔线只会在菜单顶部留下一段突兀的空白。
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        flyout.Items.Add(CreateMenuItem("全选", SelectAllGlyph, OnSelectAllClick, "Ctrl+A"));
        flyout.Items.Add(CreateMenuItem("不选择任何项目", ClearGlyph, OnSelectNoneClick, "Esc, Ctrl+D"));
    }

    /// <summary>清空并重建菜单内容；每次打开都重建，勾选与可用态按当前状态生成，无需反向同步。</summary>
    private static void RebuildMenu(MenuFlyout flyout, Action<IList<MenuFlyoutItemBase>> build)
    {
        flyout.Items.Clear();
        build(flyout.Items);
    }

    private static MenuFlyoutItem CreateMenuItem(
        string text,
        string glyph,
        RoutedEventHandler handler,
        string? acceleratorText = null)
    {
        var item = new MenuFlyoutItem
        {
            Icon = new FontIcon { Glyph = glyph },
            KeyboardAcceleratorTextOverride = acceleratorText,
            Text = text
        };

        item.Click += handler;

        return item;
    }

    private static MenuFlyoutSubItem CreateSubMenu(
        string text,
        string glyph,
        Action<IList<MenuFlyoutItemBase>> build)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Icon = new FontIcon { Glyph = glyph },
            Text = text
        };

        build(subItem.Items);

        return subItem;
    }

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
