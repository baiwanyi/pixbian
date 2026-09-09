/**
 * 图库页代码后置（M2）。
 * 职责：把界面事件转交 ViewModel，管理网格 / 自适应两种视图的切换与工具栏交互，
 *      并在条目容器进入视口时触发缩略图按需加载。
 *      条目收藏交互：单击图片延迟 300ms 打开查看器（避让双击手势），双击图片或
 *      单击右下角收藏按钮均切换收藏并播放图标弹跳动画。
 * 复用约定：所有数据操作一律委托 ViewModel，本页面不写查询、不碰数据库；
 *          视图与缩略图尺寸变更统一经 ShellViewModel.SaveSettingsAsync 持久化并广播，
 *          再由 MainWindow.ApplySettings 回流应用，本页面不直接写设置。
 *          工具栏溢出用 AdaptiveTrigger + VisualState 声明（规范 §2.3【必须】），不监听 SizeChanged；
 *          被收起的命令由「更多」菜单按各按钮当前 Visibility 补齐，带下拉菜单的按钮以同名子菜单提供，
 *          菜单内容每次 Opening 时按当前状态重建，故无需维护菜单项引用去反向同步勾选。
 * 关键约束：两种视图都用条目集合的非分组 GridView。自适应视图外层 ScrollViewer 统一滚动，
 *          JustifiedPanel 不做虚拟化（P2 换 ItemsRepeater + VirtualizingLayout）；
 *          方形视图 GridView 自滚（ItemsWrapGrid 原生虚拟化，边长由代码后置动态计算）。
 *          ContainerContentChanging 是容器生成/复用的「进入视口」时机，必须在此触发按需加载，
 *          该事件是同步的，不 await 加载结果；
 *          滚动停止时按面板行偏移表（自适应）或均匀行高（方形）求视口窗口：窗口内被瘦身
 *          条目恢复解码、滚出窗口的条目取消在途解码——自适应面板容器不回收，
 *          被瘦身条目没有其他重解触发点；方形视图容器回收重建会再次触发本事件兜底。
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Pixbian.Controls;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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

    /// <summary>等高视图（ItemsRepeater）虚拟化布局引用。</summary>
    private JustifiedVirtualizingLayout? _justifiedLayout;

    /// <summary>等高视图的虚拟化布局引用（首次访问时惰性取用）。</summary>
    /// <remarks>
    /// 布局对象是 <c>ItemsRepeater.Layout</c> 的属性值：<c>VirtualizingLayout</c> 只是
    /// DependencyObject 而非 UIElement，不在视觉树中——用 VisualTreeHelper 遍历
    /// （FindDescendant）永远返回 null，必须直接经 Repeater.Layout 取。
    /// 诊断期把布局临时换成 StackLayout 等非本类型时会返回 null，各调用点按空引用处理。
    /// </remarks>
    private JustifiedVirtualizingLayout? JustifiedLayoutCore =>
        _justifiedLayout ??= JustifiedRepeater.Layout as JustifiedVirtualizingLayout;

    // 右键菜单命中的目标项；菜单内各操作据此执行，避免依赖可能过期的 SelectedItem。
    private MediaItemViewModel? _contextItem;

    private bool _isGridView;
    private bool _isJustifiedView = true;
    private bool _isSelectionMode;
    private bool _hasSelection;

    /// <summary>
    /// 普通模式点击图片主体后置位的一次性标志：ItemClick 先于 SelectionChanged 触发时，
    /// OnItemClick 里的 Clear 清的是尚未加入的空集合、拦不住点击副作用，须由下一次
    /// SelectionChanged 消费本标志跳过自动进入并清除选择。点击复选框不触发 ItemClick，不受影响。
    /// </summary>
    private bool _suppressAutoEnterOnce;

    /// <summary>模式切换内部调整选择集合期间为 true，抑制 SelectionChanged 的自动进出联动。</summary>
    private bool _isRestructuringSelection;

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

    /// <summary>当前被按压的条目容器；松开 / 取消 / 失去捕获时回弹并清空。</summary>
    private GridViewItem? _pressedItem;

    // 按压反馈参数：下压深度与两段弹簧阻尼（按下临界阻尼干脆；回弹欠阻尼产生一次轻柔回弹）。
    private const float PressedScale = 0.98f;
    private const float PressSpringDamping = 0.9f;
    private const float ReboundSpringDamping = 0.65f;
    private static readonly TimeSpan SpringPeriod = TimeSpan.FromMilliseconds(35);
    private MediaSortKey _sortKey = MediaSortKey.ModifiedDate;
    private SortDirection _sortDirection = SortDirection.Descending;
    private bool _hasItems;
    private string _selectionCountText = "选择项目";
    private bool _isEmpty = true;

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

    /// <summary>正在按数据重建菜单勾选态；期间 CheckBox 触发的 Checked / Unchecked 一律忽略，
    /// 否则程序化赋值会被当成用户操作回写一遍。</summary>
    private bool _isApplyingGroupSelection;

    /// <summary>收藏下拉菜单本次打开期间是否改动过分组归属；关闭时据此决定是否退出选择模式。</summary>
    private bool _isFavoriteMenuDirty;

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

    /// <summary>网格项间距（相邻格子间隙，由容器模板 Margin 承担）。</summary>
    private const double GridSpacing = 8;

    /// <summary>内容区水平 Padding 总量（左右各 48），格子边长计算时扣除。</summary>
    private const double GridViewHorizontalPadding = 96;

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

        // 行高变化经依赖属性回调触发虚拟化布局重建行表（临时 StackLayout 诊断模式下无布局引用）。
        if (JustifiedLayoutCore is { } layout)
        {
            layout.RowHeight = ViewModel.ThumbnailSize;
        }
    }

    /// <summary>空状态可见性：仅在「非查询中且无内容」时显示；查询中由 loading 覆盖层接管，避免穿帮。</summary>
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

    /// <summary>当前集合是否已完成首次视口上报。</summary>
    /// <remarks>视口窗口此前只由滚动（ScrollViewer.ViewChanged）驱动建立：加载完成但用户
    /// 不滚动时窗口恒为 (-1,-1)，淘汰回调会把视窗内的条目一并置空。集合替换后重置，
    /// 由首个 realize 的元素补齐一次上报。</remarks>
    private bool _viewportReported;

    /// <summary>等高视图的元素重建是否被推迟：集合在视图不可见（方形模式）期间被替换时置位。</summary>
    private bool _justifiedItemsDirty;

    /// <summary>模板内复选框与悬停遮罩的名称（供代码后置按名命中）。</summary>
    private const string SelectionCheckName = "SelectionCheck";

    private const string HoverMaskName = "MaskBorder";

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

    /// <summary>等高视图复选框点击：普通模式先进入选择模式（保留既有选择），随后翻转选中。</summary>
    private void OnJustifiedCheckClick(object sender, RoutedEventArgs e)
    {
        // 与图片主体走同一套命中解析（按 Repeater 索引映射取条目）：复选框的 DataContext
        // 与图片绑定的 DataContext 同源，一旦元素复用残留，两者会一起错位，取哪个都错。
        if (ResolveHit(sender as DependencyObject).Item is not { } item)
        {
            return;
        }

        if (!IsSelectionMode)
        {
            EnterSelectionMode(clearExisting: false);
        }

        ViewModel.JustifiedSelection.Toggle(item);
    }

    /// <summary>等高视图选择集合变化：聚合选中项并驱动选择模式的自动进出与计数。</summary>
    private void OnJustifiedSelectionChanged(object? sender, EventArgs e)
    {
        if (_isRestructuringSelection)
        {
            return;
        }

        Selection = ViewModel.JustifiedSelection.SelectedItems;
        HasSelection = Selection.Count > 0;

        if (!IsSelectionMode && HasSelection)
        {
            EnterSelectionMode(clearExisting: false);
            return;
        }

        if (IsSelectionMode && !HasSelection)
        {
            ExitSelectionMode();
            return;
        }

        UpdateSelectionCount();
    }

    /// <summary>宽高比批量写回完成：等高虚拟化布局重建行几何表（行划分可能变化）。</summary>
    /// <remarks>InvalidateMeasure 不得在布局 pass 内同步调用，TryEnqueue 延到下一消息。</remarks>
    private void OnAspectRatiosApplied(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => JustifiedLayoutCore?.InvalidateRows());

    /// <summary>聚合当前视图的选中项：网格视图取主控件，等高视图取选择服务。</summary>
    private List<MediaItemViewModel> CollectSelection()
    {
        if (IsGridView)
        {
            return GridViewControl.SelectedItems.OfType<MediaItemViewModel>().ToList();
        }

        return [.. ViewModel.JustifiedSelection.SelectedItems];
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Selection = CollectSelection();
        HasSelection = Selection.Count > 0;

        // 模式切换内部调整选择集合期间抑制自动进出，防止误触发（见 EnterSelectionMode/ExitSelectionMode）。
        if (_isRestructuringSelection)
        {
            return;
        }

        // 消费一次性标志：本次选择变化源于点击图片主体（ItemClick 先行打标），不得进入选择模式，
        // 并清除点击副作用；清除引发的再次 SelectionChanged 为空选择、无分支命中，稳定收敛。
        var clickedItemBody = _suppressAutoEnterOnce;
        _suppressAutoEnterOnce = false;

        // 普通模式下仅 hover 复选框勾选（不触发 ItemClick）才进入选择模式（照片应用式交互）。
        if (!IsSelectionMode && HasSelection)
        {
            if (clickedItemBody && sender is GridView grid)
            {
                grid.SelectedItems.Clear();
                return;
            }

            EnterSelectionMode(clearExisting: false);
            return;
        }

        // 选择模式下取消了所有选中：自动退出选择模式（逐个取消勾选、清空快捷键、
        // 删除完最后一个选中项，均经此路径退出）。
        if (IsSelectionMode && !HasSelection)
        {
            ExitSelectionMode();
            return;
        }

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

            // Extended 模式单击图片主体会顺手把条目加入选择集合。普通模式单击的语义是「设为当前项」，
            // 选择集合必须保持为空。事件时序不定：ItemClick 在前则此处 Clear 无效，须由
            // _suppressAutoEnterOnce 让下一次 SelectionChanged 拦截（见 OnSelectionChanged）；
            // ItemClick 在后则此处 Clear 直接生效。两路并存覆盖所有时序。
            _suppressAutoEnterOnce = true;
            if (sender is GridView grid)
            {
                grid.SelectedItems.Clear();
            }
        }
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

    /// <summary>下拉菜单「仅加入收藏（不分组）」：清掉全部分组归属并置收藏，语义与未分组一致；
    /// 这是终结性操作，收工即关闭菜单（关闭时统一退出选择模式）。</summary>
    private async void OnAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        await ViewModel.ApplySelectionUngroupedAsync(Selection);

        dispatcher.TryEnqueue(() =>
        {
            _isFavoriteMenuDirty = true;
            RefreshGroupCounts();
            FavoriteGroupFlyout.Hide();
        });
    }

    /// <summary>收藏菜单关闭：本次打开期间改过归属就退出选择模式。</summary>
    /// <remarks>勾选分组时菜单不自动关闭（可连勾多个组），故退出推迟到关闭时，
    ///          避免勾完第一个分组就打断操作；未做任何改动时关闭不退出，保留现场。</remarks>
    private void OnFavoriteGroupFlyoutClosed(object sender, object e)
    {
        if (!_isFavoriteMenuDirty)
        {
            return;
        }

        _isFavoriteMenuDirty = false;
        ExitSelectionMode();
    }

    /// <summary>收藏下拉菜单打开：按选中项的实际分组归属重建勾选态。</summary>
    private async void OnFavoriteGroupFlyoutOpening(object sender, object e)
    {
        _isFavoriteMenuDirty = false;

        var selection = Selection;

        if (selection.Count == 0)
        {
            return;
        }

        var byMedia = await FavoriteGroups.GetGroupIdsByMediaAsync(selection.Select(i => i.Id).ToList());

        _isApplyingGroupSelection = true;

        try
        {
            GroupOptions.Clear();

            foreach (var group in FavoriteGroups.Groups)
            {
                var members = byMedia.Values.Count(ids => ids.Contains(group.Id));

                GroupOptions.Add(new FavoriteGroupOption
                {
                    GroupId = group.Id,
                    Name = group.Name,
                    IsChecked = members == selection.Count
                });
            }
        }
        finally
        {
            _isApplyingGroupSelection = false;
        }
    }

    /// <summary>勾选分组：把选中条目加入该组（隐含置收藏）。分组之间互不排斥，可连续勾多个。</summary>
    private async void OnFavoriteGroupChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingGroupSelection || sender is not CheckBox { Tag: FavoriteGroupOption option })
        {
            return;
        }

        // 仓储内部 ConfigureAwait(false)，await 之后不在 UI 线程，队列须提前捕获。
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        await ViewModel.ApplySelectionGroupAsync(Selection, option.GroupId, isMember: true);

        dispatcher.TryEnqueue(() =>
        {
            _isFavoriteMenuDirty = true;
            RefreshGroupCounts();
        });
    }

    /// <summary>取消勾选分组：把选中条目移出该组，不动收藏状态。</summary>
    private async void OnFavoriteGroupUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingGroupSelection || sender is not CheckBox { Tag: FavoriteGroupOption option })
        {
            return;
        }

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        await ViewModel.ApplySelectionGroupAsync(Selection, option.GroupId, isMember: false);

        dispatcher.TryEnqueue(() =>
        {
            _isFavoriteMenuDirty = true;
            RefreshGroupCounts();
        });
    }

    /// <summary>刷新分组集合：成员数变化后侧栏子项与设置页计数才跟得上。</summary>
    private void RefreshGroupCounts() => _ = FavoriteGroups.LoadAsync();

    /// <summary>选择工具栏「删除」：把选中项移入回收站，进度与结果经底部通知条呈现。</summary>
    private async void OnDeleteSelectionClick(object sender, RoutedEventArgs e)
    {
        await DeleteContextItemsAsync(Selection);
    }

    /// <summary>点击「选择」按钮：进入勾选式选择模式，所有列表切换为多选并清空既有选择。</summary>
    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        EnterSelectionMode(clearExisting: true);
    }

    /// <summary>进入勾选式选择模式，两个视图的所有列表切换为多选。</summary>
    /// <param name="clearExisting">true 清空既有选择（工具栏「选择」入口）；false 保留（条目复选框入口）。</param>
    /// <remarks>切换 SelectionMode 会重置各列表的选择，故先抓快照，切换归零后再按需恢复，
    /// 保证复选框入口勾选的第一项不丢。内部调整选择须抑制 SelectionChanged 的自动退出。</remarks>
    private void EnterSelectionMode(bool clearExisting)
    {
        var mainSelection = GridViewControl.SelectedItems.OfType<object>().ToList();

        _isRestructuringSelection = true;

        try
        {
            IsSelectionMode = true;

            GridViewControl.SelectionMode = ListViewSelectionMode.Multiple;

            // 切换 SelectionMode 可能已清空选择，统一归零后按快照恢复，避免重复添加。
            GridViewControl.SelectedItems.Clear();

            if (!clearExisting)
            {
                foreach (var item in mainSelection)
                {
                    GridViewControl.SelectedItems.Add(item);
                }
            }
            else
            {
                // 工具栏「选择」入口清空全部视图的既有选择（等高视图选择在服务内）。
                ViewModel.JustifiedSelection.Clear();
            }

            SyncJustifiedCheckVisibility();
            UpdateSelectionCount();
        }
        finally
        {
            _isRestructuringSelection = false;
        }

        // 首次进入选择模式会引发一轮布局风暴（全量可见条目的复选框显示、页头整行替换），
        // 程序化焦点若同帧执行会叠加焦点遍历与同步布局造成可感卡顿；延后一帧错峰。
        DispatcherQueue.TryEnqueue(() => RestoreContentFocus());
    }

    /// <summary>退出选择模式并清空选择；工具栏「取消」与「取消所有选中」的自动退出共用。</summary>
    private void ExitSelectionMode()
    {
        _isRestructuringSelection = true;

        try
        {
            IsSelectionMode = false;

            GridViewControl.SelectionMode = ListViewSelectionMode.Extended;
            GridViewControl.SelectedItems.Clear();
            ViewModel.JustifiedSelection.Clear();

            // 兜底复位：已回收但残留的 Repeater 元素上可能还绑着历史条目的选中态，选择服务
            // 清不到它们（既不在 _selected 也不在当前集合），退出时按当前集合全量复位，
            // 否则悬停显示复选框时还能看到不属于本次选择的勾。
            foreach (var item in ViewModel.Items)
            {
                item.IsSelected = false;
            }

            SyncJustifiedCheckVisibility();

            Selection = [];
            HasSelection = false;
            SelectionCountText = "选择项目";
        }
        finally
        {
            _isRestructuringSelection = false;
        }

        RestoreContentFocus();
    }

    /// <summary>把选择模式的复选框可见性批量同步到全部条目（等高 Repeater 模板由条目属性驱动）。</summary>
    private void SyncJustifiedCheckVisibility()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsSelectionCheckVisible = IsSelectionMode;
        }

        // 模式切换会改写复选框的绑定值（Opacity / IsHitTestVisible），悬停态写下的元素本地值
        // 随之失效，丢弃悬停引用即可；不要在这里回写本地值，那会压住下一次绑定推送。
        _hoveredElement = null;
    }

    /// <summary>点击「取消」：退出选择模式并清空选择。</summary>
    private void OnCancelSelectionClick(object sender, RoutedEventArgs e)
    {
        ExitSelectionMode();
    }

    /// <summary>把焦点设回内容区列表（仅在焦点已丢失或落到页面之外时）。</summary>
    /// <remarks>
    /// 触发模式切换的按钮随所在行整体隐藏、从视觉树卸载，框架会把焦点自动转移到
    /// Tab 序中下一个可聚焦控件（搜索框）；焦点落在文本框后，Delete 等按键会被当作
    /// 文本编辑消费，页面 KeyDown 收不到——只有这种情况才需要还原焦点。
    /// 焦点仍在本页视觉树内（如用户刚点过的条目复选框）时**不抢**：
    /// 每次模式切换 / 删除都把焦点设到列表框，会打断用户的键盘位置感。
    /// </remarks>
    private void RestoreContentFocus()
    {
        if (IsFocusWithinPage())
        {
            return;
        }

        // 等高视图焦点给外层 ScrollViewer（Repeater 无内建焦点链，键事件经页面级 KeyDown）。
        // ScrollViewer 默认 IsTabStop=False，Focus 会静默失败（返回 false），焦点就会留在
        // 框架自动转移的搜索框里，DEL 等键全被文本编辑消费——JustifiedView 必须显式
        // IsTabStop=True（见 XAML），此处不检查返回值是有前提的。
        if (IsJustifiedView)
        {
            JustifiedView.Focus(FocusState.Programmatic);
            return;
        }

        GridViewControl.Focus(FocusState.Programmatic);
    }

    /// <summary>最近一次获得焦点的元素：供焦点还原前判定焦点是否仍在本页视觉树内。</summary>
    private DependencyObject? _lastFocusedElement;

    private void OnFocusManagerGotFocus(object? sender, FocusManagerGotFocusEventArgs args)
        => _lastFocusedElement = args.NewFocusedElement;

    /// <summary>最近获焦元素是否仍在本页视觉树内；元素已被卸载（如删除的条目复选框）视为否。</summary>
    private bool IsFocusWithinPage()
    {
        for (var node = _lastFocusedElement; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>批量切换选中态：方形视图经 GridView，等高视图经选择服务。</summary>
    /// <param name="select">true 表示全选，false 表示清空。</param>
    private void SelectAll(bool select)
    {
        ToggleAllGrid(GridViewControl, select);

        if (select)
        {
            ViewModel.JustifiedSelection.SelectAll();
        }
        else
        {
            ViewModel.JustifiedSelection.Clear();
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

    /// <summary>刷新选择计数标题：未选时显示模式提示，选中后显示数量。</summary>
    private void UpdateSelectionCount()
    {
        var count = CollectSelection().Count;
        SelectionCountText = count == 0 ? "选择项目" : $"已选择 {count} 个项目";
    }

    /// <summary>键盘快捷键：F5 从头开始幻灯片播放；Ctrl+A 全选；Ctrl+D / Esc 取消选择；
    /// Ctrl+C 复制文件；Delete 移入回收站；F2 重命名；F3 在资源管理器中打开。</summary>
    /// <remarks>
    /// 一律用 KeyDown 处理，不用 XAML 的 Page.KeyboardAccelerators：注册快捷键后，框架会按官方设计
    /// 把按键组合追加到作用域内「所有控件」的 ToolTip 上（MenuFlyoutItem 除外），
    /// 导致 hover 图片时文件名提示里混入「Ctrl+A」。没有 accelerator 就没有该行为。
    /// 作用域为「焦点在本页内」，与项目其他快捷键（Ctrl+C / Delete / F2）一致。
    /// </remarks>
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

            if (e.Key == VirtualKey.A && !e.Handled)
            {
                e.Handled = true;

                // 非选择模式下条目不显示复选框与遮罩，直接全选不会有任何视觉反馈，故先切模式
                // （OnSelectClick 会清空既有选择，随后再全选，顺序不可颠倒）。
                if (!IsSelectionMode)
                {
                    OnSelectClick(this, new RoutedEventArgs());
                }

                SelectAll(true);
            }

            if (e.Key == VirtualKey.D && !e.Handled)
            {
                e.Handled = true;
                SelectAll(false);
            }

            return;
        }

        if (e.Key == VirtualKey.Escape && !e.Handled)
        {
            e.Handled = true;
            SelectAll(false);
            return;
        }

        if (e.Key == VirtualKey.Delete && !e.Handled)
        {
            e.Handled = true;

            var targets = GetContextTarget();

            _ = DeleteContextItemsAsync(targets);
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

        // 滚动停止：按视口窗口驱动恢复与取消（O(log n + 窗口)，与总条目数无关），
        // 取代原「全集合 × ContainerFromItem」扫描。
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

        // 等高视图：虚拟化布局的行几何表（与 JustifiedPanel 同签名）。
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
        // 视图从 Collapsed 变可见后模板才展开，内部滚动条在此前不存在——此处兜底补登记。
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
    /// 阻断「滚动条 ↔ 边长」布局震荡（JustifiedPanel 时代实证过的坑）。
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

    /// <summary>在图片上右键：定位命中项，弹出上下文菜单并填充只读信息项。</summary>
    /// <remarks>
    /// 右键命中项作为本次菜单的操作目标，统一经 _contextItem 传递，避免依赖可能过期的 SelectedItem；
    /// 菜单由 CreateItemContextMenu 在代码中构建（资源字典不带 x:Class，无法用 x:Name 绑定事件），
    /// Click 在构建时逐个挂接，故此处只需填充信息项。
    /// </remarks>
    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var (item, _) = ResolveHit(e.OriginalSource as DependencyObject);

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
        var flyout = CreateItemContextMenu(ActualTheme);

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

        // 订阅 Opened / Closed：在 Closed 里统一解绑 Click 与这两个事件，
        // 否则下次弹出会重复订阅，一次点击触发多次。
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
    private static MenuFlyout CreateItemContextMenu(ElementTheme theme)
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

        // 删除项：前景取统一的删除色（浅色 #C42B1C / 深色 #FF99A4，随主题从 ThemeDictionaries 取键），
        // 且 hover/pressed 视觉状态（默认模板会把 TextBlock.Foreground 改回主题键
        // MenuFlyoutItemForegroundPointerOver/Pressed）通过项级主题键覆盖保持红色，
        // 不重写 ControlTemplate（避免触发旋转忙碌光标）。
        var deleteBrush = (SolidColorBrush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[
            theme == ElementTheme.Dark ? "Dark" : "Default"])["PixbianDeleteForeground"];
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

    /// <summary>弹出前填充只读信息项（大小 / 尺寸 / 日期）。</summary>
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
        // 结尾分隔符必须去掉：`/select,"D:\Lib\"` 中 `\` 会转义收尾引号，explorer 解析失败。
        var target = filePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>把目标集合首个文件复制到剪贴板（StorageItem 方式，支持跨应用粘贴）。</summary>
    private async Task CopyItemToClipboardAsync(IReadOnlyList<MediaItemViewModel> targets)
    {
        var first = targets.Count > 0 ? targets[0] : null;

        if (first is null)
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(first.Item.Path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(new[] { file });
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // 目标文件可能已被外部删除 / 移动（页面 Selection 短暂过期等）：fire-and-forget
            // 的异常是黑洞，静默吞掉用户只会看到「按了没反应」，必须反馈。
            await ShowErrorAsync("复制失败", ex.Message);
        }
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

    /// <summary>把目标集合移入回收站，并同步从索引与视图集合中移除；进度与结果经通知条呈现。</summary>
    /// <remarks>失败不再弹错误对话框：删除通知条统一呈现结果（成功 / 取消 / 部分失败）。</remarks>
    private async Task DeleteContextItemsAsync(IReadOnlyList<MediaItemViewModel> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        await ViewModel.DeleteFilesAsync(targets);
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

    /// <summary>从选中项（或首个条目）开始幻灯片放映：由外壳打开放映窗口接管；
    /// 视频是否参与放映由设置决定，起点为视频且被过滤时放映视图模型会回落到首个条目。</summary>
    private async void OnSlideShowClick(object sender, RoutedEventArgs e)
    {
        var start = Selection.Count > 0
            ? Selection[0]
            : (ViewModel.Items.Count > 0 ? ViewModel.Items[0] : null);

        if (start is null)
        {
            return;
        }

        await Owner.OpenSlideShowAsync(ViewModel.Items, start);
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

    /// <summary>选择工具栏「更多」菜单打开时重建：先放被收起的命令（当前只有「播放」），
    /// 再放全选 / 不选择任何项目。</summary>
    private void OnSelectionMoreMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Items.Clear();

        if (SelectionPlayButton.Visibility == Visibility.Collapsed)
        {
            var play = CreateMenuItem("播放", SlideShowGlyph, OnSlideShowClick);
            play.IsEnabled = HasSelection;
            flyout.Items.Add(play);
        }

        // 宽度足够时没有收起任何命令，分隔线只会在菜单顶部留下一段突兀的空白。
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        flyout.Items.Add(CreateMenuItem("全选", SelectAllGlyph, OnSelectAllClick, "Ctrl+A"));
        flyout.Items.Add(CreateMenuItem("不选择任何项目", ClearGlyph, OnSelectNoneClick, "Esc, Ctrl+D"));
    }

    /// <summary>取消正在进行的删除：已移入回收站的部分保留。</summary>
    private void OnCancelDeleteClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelDelete();
    }

    /// <summary>手动关闭删除结果通知条。</summary>
    private void OnCloseDeleteResultClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseDeleteResult();
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
