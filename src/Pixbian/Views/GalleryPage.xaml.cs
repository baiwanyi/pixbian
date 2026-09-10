/**
 * 图库页代码后置（主文件）。
 * 职责：持有视图模型与页面级状态、把界面事件转交 ViewModel，管理网格 / 自适应两种视图的
 *      切换与等高元素重建；分支职责见各 partial——条目交互与收藏手势（.ItemInteraction.cs）、
 *      选择模式与键盘（.Selection.cs）、方形网格视图（.GridView.cs）、右键菜单与文件操作
 *      （.ContextMenu.cs）、工具栏菜单（.Toolbars.cs）。
 * 复用约定：所有数据操作一律委托 ViewModel，本页面不写查询、不碰数据库；
 *          视图与缩略图尺寸变更统一经 ShellViewModel.SaveSettingsAsync 持久化并广播，
 *          再由 MainWindow.ApplySettings 回流应用，本页面不直接写设置。
 * 关键约束：两种视图都用条目集合的非分组 GridView。自适应视图外层 ScrollViewer 统一滚动，
 *          条目经 ItemsRepeater + JustifiedVirtualizingLayout 虚拟化；
 *          方形视图 GridView 自滚（ItemsWrapGrid 原生虚拟化，边长由代码后置动态计算）。
 *          ContainerContentChanging 是容器生成/复用的「进入视口」时机，必须在此触发按需加载，
 *          该事件是同步的，不 await 加载结果；等高 Repeater 在集合整体替换时由框架
 *          CollectionChanged 路径重建元素，页面层只负责回顶与重置页面级瞬态状态。
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
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
    /// <summary>等高视图（ItemsRepeater）虚拟化布局引用。</summary>
    private JustifiedVirtualizingLayout? _justifiedLayout;

    /// <summary>等高视图的虚拟化布局引用（首次访问时惰性取用）。</summary>
    /// <remarks>
    /// 布局对象是 <c>ItemsRepeater.Layout</c> 的属性值：<c>VirtualizingLayout</c> 只是
    /// DependencyObject 而非 UIElement，不在视觉树中——用 VisualTreeHelper 遍历
    /// （FindDescendant）永远返回 null，必须直接经 Repeater.Layout 取。
    /// 布局若被换成非本类型（如 StackLayout），此处返回 null，各调用点按空引用处理。
    /// </remarks>
    private JustifiedVirtualizingLayout? JustifiedLayoutCore =>
        _justifiedLayout ??= JustifiedRepeater.Layout as JustifiedVirtualizingLayout;

    private bool _isGridView;
    private bool _isJustifiedView = true;
    private bool _hasItems;
    private string _selectionCountText = "选择项目";
    private bool _isEmpty = true;

    /// <summary>当前集合是否已完成首次视口上报。</summary>
    /// <remarks>视口窗口只由滚动（ScrollViewer.ViewChanged）驱动建立：加载完成但用户
    /// 不滚动时窗口恒为 (-1,-1)，淘汰回调会把视窗内的条目一并置空。集合替换后重置，
    /// 由首个 realize 的元素补齐一次上报。</remarks>
    private bool _viewportReported;

    /// <summary>等高视图的元素重建是否被推迟：集合在视图不可见（方形模式）期间被替换时置位。</summary>
    private bool _justifiedItemsDirty;

    /// <summary>初始化图库页。</summary>
    /// <param name="viewModel">图库视图模型，由依赖注入提供。</param>
    /// <param name="shell">应用外壳视图模型，用于持久化视图与缩略图尺寸设置。</param>
    public GalleryPage(
        GalleryViewModel viewModel,
        ShellViewModel shell,
        FavoriteGroupViewModel favoriteGroups)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(favoriteGroups);

        ViewModel = viewModel;
        _shell = shell;
        FavoriteGroups = favoriteGroups;
        ViewModel.JustifiedSelection.SelectionChanged += OnJustifiedSelectionChanged;
        ViewModel.AspectRatiosApplied += OnAspectRatiosApplied;
        ViewModel.ItemsReplaced += OnItemsReplaced;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 网格面板的尺寸绑定使用传统 Binding，需要显式提供 DataContext。
        DataContext = this;

        InitializeComponent();

        // F5 快捷键从头开始幻灯片播放。
        KeyDown += OnGalleryPageKeyDown;

        // 焦点跟踪：FocusManager.GetFocusedElement 在键处理栈内有返回 null 的怪癖（KEY 取证实测），
        // 判断「焦点是否仍在本页」只能经 GotFocus 全局事件记录最近获焦元素。随 Loaded/Unloaded
        // 订退，避免静态事件持有页面引用。
        Loaded += (_, _) =>
        {
            FocusManager.GotFocus -= OnFocusManagerGotFocus;
            FocusManager.GotFocus += OnFocusManagerGotFocus;
        };
        Unloaded += (_, _) => FocusManager.GotFocus -= OnFocusManagerGotFocus;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>图库视图模型。</summary>
    public GalleryViewModel ViewModel { get; }

    /// <summary>收藏分组视图模型；与设置页、主窗口侧栏共享同一集合。</summary>
    public FavoriteGroupViewModel FavoriteGroups { get; }

    /// <summary>收藏下拉菜单的勾选项；每次菜单打开时按选中项的实际归属重建。</summary>
    public ObservableCollection<FavoriteGroupOption> GroupOptions { get; } = [];

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
    /// <remarks>等高模板复选框显隐由条目属性驱动，进出选择模式时批量同步（Enter/Exit）。</remarks>
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

    /// <summary>应用视图模式，切换两种视图的可见性。</summary>
    /// <remarks>菜单勾选不在此同步：下拉菜单每次 Opening 时按当前设置重建，勾选态天然最新。</remarks>
    /// <param name="viewMode">目标视图模式。</param>
    public void ApplyViewMode(GalleryViewMode viewMode)
    {
        IsGridView = viewMode == GalleryViewMode.Grid;
        IsJustifiedView = viewMode == GalleryViewMode.Justified;

        // 集合是在等高视图不可见（方形模式）期间被替换的：此刻宿主才变为可见，
        // 补上被推迟的重建，否则 Repeater 会直接复用上一目录的元素。
        if (IsJustifiedView && _justifiedItemsDirty)
        {
            ApplyPendingJustifiedItems();
        }
    }

    /// <summary>应用缩略图尺寸变化，刷新方形格子边长与等高虚拟化布局行高。</summary>
    public void ApplyThumbnailSize()
    {
        // 归零行数门控：档位变化必须重算边长，即使每行个数恰好不变。
        _wrapPerRow = 0;
        UpdateWrapGridCellSize(GridViewControl.ActualWidth);

        // 行高变化经依赖属性回调触发虚拟化布局重建行表。
        if (JustifiedLayoutCore is { } layout)
        {
            layout.RowHeight = ViewModel.ThumbnailSize;
        }
    }

    /// <summary>空状态可见性：仅在「非查询中且无内容」时显示；查询中整区隐藏，避免穿帮。</summary>
    public bool ShowEmptyState => IsEmpty && !ViewModel.IsQuerying;

    /// <summary>空状态主文案：区分真空目录与加载失败；加载期整区隐藏，不出现本文案。</summary>
    public string EmptyStateTitle => ViewModel.IsLoadFailed ? "加载失败，请重试" : "没有照片或视频";

    /// <summary>空状态副文案可见性：仅真空目录显示操作指引；失败的原因提示已在主文案与统计行。</summary>
    public bool ShowEmptySubtitle => ShowEmptyState && !ViewModel.IsLoadFailed;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ItemCount：集合整体替换（重置 / 首屏）与删除路径都会通知；IsQuerying / IsLoadFailed：加载状态机。
        if (e.PropertyName is nameof(GalleryViewModel.ItemCount)
            or nameof(GalleryViewModel.IsQuerying)
            or nameof(GalleryViewModel.IsLoadFailed))
        {
            IsEmpty = ViewModel.ItemCount == 0;
            HasItems = ViewModel.ItemCount > 0;
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(ShowEmptySubtitle));

            // 新集合的条目需要当前选择模式的复选框可见性（等高模板由条目属性驱动）。
            SyncJustifiedCheckVisibility();

            // 回顶与元素重建只属于「集合整体替换」（ItemsReplaced 事件）：
            // ItemCount 通知由替换、翻页追加、删除三条路径共用，翻页后若在此回顶，
            // 会把刚触发的翻页弹回首屏，表现为「滚到底不加载更多、反而回到顶部」。
        }

        // 切换视图时立即回顶：ItemsSource 整体替换后 ScrollViewer 会保留旧偏移，
        // 新内容从中部开始显示，表现为「滚动条没有置顶」。
        if (e.PropertyName == nameof(GalleryViewModel.IsQuerying) && ViewModel.IsQuerying)
        {
            // 切换目录 / 筛选 / 搜索一律先退出选择模式：选择集合与「是否在选择模式」都是
            // 页面级状态，跨集合残留时会在集合替换的**中途**被 GridView 的选择变化触发一次
            // 模式退出——页头整行替换会改变内容区尺寸，迫使 Repeater 在幽灵元素回收完成前
            // 重新布局，正是「首格显示上一个列表内容」的诱因。先退出，布局在换集合前稳定。
            if (IsSelectionMode)
            {
                ExitSelectionMode();
            }

            JustifiedView.ChangeView(null, 0, null, true);

            // GridView 无 ChangeView API：经其内部滚动条回顶（未加载时无需回顶，新集合本就从顶部开始）。
            (_gridViewer ?? FindDescendant<ScrollViewer>(GridViewControl))?.ChangeView(null, 0, null, true);
        }
    }

    /// <summary>等高视图集合替换：强制 Repeater 走全新 realize 路径，杜绝元素复用残留。</summary>
    /// <remarks>由 <see cref="GalleryViewModel.ItemsReplaced"/> 事件驱动（仅整体替换时触发）；
    /// ItemCount 通知共用度太高（翻页追加 / 删除也发），不能作为本方法的触发源。</remarks>
    /// <remarks>
    /// ItemsRepeater 在 ItemsSource 换实例后可能保留旧元素映射——复用路径不触发
    /// ElementPrepared，DataContext 与位图停留在上一个目录的条目上，表现为「列表前几项
    /// 显示不属于当前目录的内容」。
    /// 「置空 → UpdateLayout → 赋新值」三步缺一不可：Repeater 只在下一次 measure 时读取
    /// ItemsSource，同一同步块内的 null 会被新值直接覆盖，等于没换。
    /// 但宿主（ScrollViewer）不可见时 UpdateLayout 不会 measure 它，null 落不了地——
    /// 集合由 ItemsRepeater 走 CollectionChanged 路径（参见 GalleryViewModel.ReplaceItemsCore
    /// 原地 Clear + Add），框架会清空旧元素并按新集合 realize，无需页面层手动 ItemsSource 重建。
    /// 本方法仅负责回顶 + 重置页面级瞬态状态；浏览器不可见时延到可见时再回顶。</remarks>
    private void RebuildJustifiedElements()
    {
        _hoveredElement = null;
        _viewportReported = false;

        if (JustifiedView.Visibility != Visibility.Visible)
        {
            _justifiedItemsDirty = true;
            return;
        }

        // ScrollViewer 会保留上一个列表的滚动位置，回顶确保 Repeater 从 idx=0 开始 realize。
        JustifiedView.ChangeView(null, 0, null, true);
    }

    /// <summary>集合整体替换（切目录 / 筛选 / 搜索 / 重载）：重建等高元素并回顶。</summary>
    private void OnItemsReplaced(object? sender, EventArgs e) => RebuildJustifiedElements();

    /// <summary>等高视图从不可见变可见时补做回顶（集合在其不可见期间被替换）。</summary>
    private void ApplyPendingJustifiedItems()
    {
        _justifiedItemsDirty = false;
        JustifiedView.ChangeView(null, 0, null, true);
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not MediaItemViewModel item)
        {
            return;
        }

        // 标记该条目已生成过容器，供滚动取消区分「从未进入视口」与「已滚出视口」。
        item.ContainerEverRealized = true;

        // 惰性登记方形视图基础设施：首个容器 realize 时面板必然已在树中
        // （容器正是由它 realize 的），此处兜住全部路径；幂等，已登记时零成本。
        if (sender == GridViewControl)
        {
            EnsureGridViewInfrastructure(GridViewControl.ActualWidth);

            // 网格视图的内容区是正方形，Uniform 不会超出它，显示区最长边恒为格子边长。
            item.SetDisplaySize(ViewModel.ThumbnailSize, ViewModel.ThumbnailSize);
        }

        // 不 await：虚拟化管线要求该事件同步返回，等待 IO 会阻塞滚动。
        _ = item.EnsureThumbnailAsync(ViewModel.ThumbnailSize);
    }

    /// <summary>宽高比批量写回完成：等高虚拟化布局重建行几何表（行划分可能变化）。</summary>
    /// <remarks>InvalidateMeasure 不得在布局 pass 内同步调用，TryEnqueue 延到下一消息。</remarks>
    private void OnAspectRatiosApplied(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => JustifiedLayoutCore?.InvalidateRows());

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
