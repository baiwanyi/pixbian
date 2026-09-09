/**
 * 主窗口代码后置（M2）：自定义标题栏 + 三栏布局外壳，负责导航切换、搜索下发、主题应用与窗口图标设置。
 * 职责：把导航项映射为页面可见性，把搜索输入转交给外壳视图模型，并响应设置变化重新应用
 *      主题、视图配置与幻灯片参数；图片查看器为独立全屏窗口（主窗口保持原样，本窗口
 *      仅负责创建/复用查看器窗口），视频播放器仍走页面替换；
 *      标题栏延伸进客户区后，交互控件须注册 Passthrough 区域才能接收指针输入；
 *      左栏「分类」「图库」为分组标题，子项由扫描源与分类集合驱动动态重建，
 *      选中子项时切到图库页按文件夹或分类过滤，分组标题内联按钮提供添加文件夹与批量重新匹配。
 * 复用约定：页面实例与视图模型均由依赖注入提供；主题映射统一在 MapTheme 中完成，
 *          领域层的 AppTheme 与 WinUI 的 ElementTheme 只在此处转换。
 * 关键约束：主题必须设置在窗口内容根元素上，设在 Window 本身对 WinUI 3 无效；
 *          Passthrough 矩形为物理像素，须按 XamlRoot.RasterizationScale 换算，且在布局与激活变化时刷新；
 *          设置页需异步加载扫描源，故导航到设置页时必须触发一次初始化，不能只在启动时加载。
 */

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using Pixbian.Core.Models;
using Pixbian.Services;
using Pixbian.ViewModels;
using Pixbian.WebServer;

namespace Pixbian.Views;

/// <summary>Pixbian 主窗口。</summary>
public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    /// <summary>图库分组子项 Tag 前缀，后跟扫描源主键。</summary>
    private const string MediaFolderTagPrefix = "media-folder:";

    /// <summary>分类分组子项 Tag 前缀，后跟分类主键。</summary>
    private const string CategoryTagPrefix = "category:";

    /// <summary>图库行右侧展开箭头的可点宽度：箭头位于行右端，此为自右边缘起算的命中范围。</summary>
    private const double GalleryChevronHitWidth = 44;

    private readonly ShellViewModel _shell;
    private readonly GalleryViewModel _gallery;
    private readonly SettingsViewModel _settings;
    private readonly ImageViewerViewModel _viewer;
    private readonly CategoryViewModel _categories;
    private readonly IThumbnailService _thumbnails;
    private readonly GalleryPage _galleryPage;
    private readonly SettingsPage _settingsPage;
    private readonly ShortPage _shortPage;

    /// <summary>当前打开的图片查看器窗口；窗口关闭（Closed）后置 null，下次打开创建新实例。</summary>
    private ImageViewerWindow? _imageViewerWindow;

    /// <summary>当前打开的幻灯片放映窗口；窗口关闭（Closed）后置 null，下次打开创建新实例。</summary>
    private SlideShowWindow? _slideShowWindow;

    private NavigationTarget _currentTarget = NavigationTarget.AllPhotos;
    private bool _isViewerVisible;
    private double _lastRasterizationScale;

    /// <summary>当前按分类过滤的主键；刷新分类完成后据此重放过滤，非分类过滤上下文为 null。</summary>
    private long? _activeCategoryFilter;

    /// <summary>图库分组的展开状态：只由右侧展开箭头改变，点行本身导航时不改。</summary>
    private bool _isGalleryExpanded = true;

    /// <summary>图库分组最近一次展开/折叠之前的状态：点行触发的切换要按它还原。</summary>
    private bool _isGalleryExpandedBeforeToggle = true;

    /// <summary>程序自行改写图库展开状态期间为 true：区别于用户点箭头，不记入 _isGalleryExpanded。</summary>
    private bool _isSyncingGalleryExpansion;

    /// <summary>本次点击落在图库行本身而非右侧箭头上：其引发的展开/折叠需要撤销。</summary>
    private bool _isGalleryContentClick;

    /// <summary>本次按下落在图库行右侧的展开箭头区：允许切换展开，且不算「点行」。</summary>
    private bool _isGalleryChevronClick;

    /// <summary>初始化主窗口。</summary>
    /// <param name="shell">外壳视图模型。</param>
    /// <param name="gallery">图库视图模型。</param>
    /// <param name="settings">设置视图模型。</param>
    /// <param name="viewer">图片查看器视图模型。</param>
    /// <param name="categories">分类视图模型，驱动左栏分类子项。</param>
    /// <param name="thumbnails">缩略图服务，用于同步显示缩放比。</param>
    /// <param name="galleryPage">图库页实例。</param>
    /// <param name="settingsPage">设置页实例。</param>
    /// <param name="shortPage">Short 页实例。</param>
    public MainWindow(
        ShellViewModel shell,
        GalleryViewModel gallery,
        SettingsViewModel settings,
        ImageViewerViewModel viewer,
        CategoryViewModel categories,
        IThumbnailService thumbnails,
        GalleryPage galleryPage,
        SettingsPage settingsPage,
        ShortPage shortPage)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(gallery);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(galleryPage);
        ArgumentNullException.ThrowIfNull(settingsPage);
        ArgumentNullException.ThrowIfNull(shortPage);

        _shell = shell;
        _gallery = gallery;
        _settings = settings;
        _viewer = viewer;
        _categories = categories;
        _thumbnails = thumbnails;
        _galleryPage = galleryPage;
        _settingsPage = settingsPage;
        _shortPage = shortPage;

        // 查看器移交放映：页面单例对窗口单例，构造期订阅一次即可（两者与主窗口同生命周期）。
        _viewer.SlideShowHandoffRequested += OnViewerSlideShowHandoffRequested;

        InitializeComponent();

        // 【临时实验】Mica 停用：与背景大图、ThemeShadow 同为窗口级合成层，一并摘除以
        // 最小化合成树复杂度（渲染冻结排除实验）。代价：窗口暂时失去 Win11 圆角，属预期。
        // SystemBackdrop = new MicaBackdrop();

        // 标题栏延伸进客户区：顶部拖拽区由系统管理，交互控件经 Passthrough 放行指针事件。
        // 系统标题栏按钮默认高度为 32 DIP，须切换为 Tall（48 DIP）才能与 48 高的标题栏行对齐。
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        RootGrid.Loaded += OnRootGridLoaded;
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        Activated += OnWindowActivated;

        // unpackaged 应用标题栏/任务栏不会自动继承 exe 图标，
        // 通过 AppWindow.SetIcon 加载随构建输出的多尺寸 ico（仅支持 .ico 文件）。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // 窗口背景图同样按磁盘路径加载，与 app.ico 同一方式：
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += OnActualThemeChanged;
        }

        // Window 不继承 FrameworkElement，没有 DataContext，故设置在根元素上。
        // AppWindow 为 WinUI 3 的 Window 内置属性，无需另行获取。
        RootGrid.DataContext = this;

        // 图库页需要回调主窗口以打开查看器；设置页需要主窗口的 WindowId 归属文件夹选择器。
        _galleryPage.Owner = this;
        _settingsPage.Owner = this;

        _shell.SettingsChanged += OnSettingsChanged;

        // 设置页的偏好（主题、幻灯片、查看器行为等）走 SettingsViewModel 落盘，
        // 与外壳的视图设置（ShellViewModel）是两条保存链路，广播事件必须都接住，
        // 否则设置页的改动只落盘不应用，重启才生效。
        _settings.SettingsChanged += OnSettingsChanged;

        // 快捷键经根网格代码后置处理：在根容器注册 KeyboardAccelerator 会让全窗口
        // 所有 ToolTip 追加速度提示（官方行为且无法关闭），故只能走 KeyDown 分支。
        RootGrid.KeyDown += OnRootGridKeyDown;

        // 图库行的右侧箭头与行本身都会抛 ItemInvoked，事件上无从区分，
        // 只能按指针落点判定。handledEventsToo：箭头区按下会被项内部标记已处理。
        GalleryNavItem.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnGalleryPointerPressed),
            handledEventsToo: true);

        // 左栏动态子项由两个视图模型的集合驱动：设置页增删扫描源、分类页增删分类后自动同步。
        _settings.Folders.CollectionChanged += OnFoldersChanged;
        _categories.Categories.CollectionChanged += OnCategoriesChanged;

        // 分类集合仅由分类管理页的 Loaded 填充，主窗口须在启动时主动加载一次，
        // 左栏「分类」子项才能与「图库」子项一样随应用启动展开显示。
        _ = _categories.LoadAsync();

        // 启动默认进入收藏夹：按 Tag 定位，避免依赖菜单项的排列顺序。
        NavigationViewControl.SelectedItem =
            FindNavItem(NavigationViewControl.MenuItems, "Favorites") ?? NavigationViewControl.MenuItems[0];

        ApplySettings(_shell.Settings);

        _ = InitializeAsync();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>设置视图模型，供设置页绑定。</summary>
    public SettingsViewModel SettingsViewModel => _settings;

    /// <summary>图库视图模型，供图库页绑定。</summary>
    public GalleryViewModel GalleryViewModel => _gallery;

    /// <summary>是否显示图库页。</summary>
    public bool IsGalleryVisible => _currentTarget is NavigationTarget.AllPhotos;

    /// <summary>是否显示视频页。</summary>
    public bool IsVideosVisible => _currentTarget is NavigationTarget.Videos;

    /// <summary>是否显示设置页。</summary>
    public bool IsSettingsVisible => _currentTarget is NavigationTarget.Settings;

    /// <summary>搜索框占位文本，随当前导航目标（图库 / 视频 / 收藏夹）变化。</summary>
    public string SearchPlaceholder =>
        _currentTarget switch
        {
            NavigationTarget.Videos => "在视频中搜索",
            NavigationTarget.Favorites => "在收藏夹中搜索",
            _ => "在图库中搜索",
        };

    /// <summary>是否处于图片查看状态，用于隐藏导航与详情面板。</summary>
    public bool IsViewerVisible
    {
        get => _isViewerVisible;
        private set
        {
            if (_isViewerVisible == value)
            {
                return;
            }

            _isViewerVisible = value;
            OnPropertyChanged();
        }
    }

    /// <summary>导航栏显示模式；查看图片时收起，以获得最大的图像显示区域。</summary>
    public NavigationViewPaneDisplayMode NavigationPaneMode =>
        IsViewerVisible ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;

    private async Task InitializeAsync()
    {
        try
        {
            await _shell.InitializeAsync();

            // 左栏图库分组的子项由扫描源集合驱动，启动时加载一次；
            // 后续增删经 CollectionChanged 自动同步，此处无需反复调用。
            await _settings.LoadCommand.ExecuteAsync(null);
            await _gallery.ReloadCommand.ExecuteAsync(null);

            // 元数据回填常驻续跑（断点续跑）：启动延迟触发，直至待处理条目清零。
            _settings.StartBackfillResidency();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"InitializeAsync failed: {ex}");
        }

        // 设置加载完成后，若上次启用了 Web 访问则自动恢复。
        // 此段独立 try/catch：Web 启动失败不应影响图库加载，反之亦然。
        if (_shell.Settings.IsWebSharingEnabled)
        {
            try
            {
                await App.Services.GetRequiredService<WebAccessServer>().StartAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Web server start failed: {ex}");
            }
        }
    }



    /// <summary>有分组被展开：转交判定，仅图库分组且非点行引发时才记为设定状态。</summary>
    private void OnNavigationItemExpanding(
        NavigationView sender,
        NavigationViewItemExpandingEventArgs args) =>
        TrackGalleryExpansion(args.ExpandingItemContainer, true);

    /// <summary>有分组被折叠：转交判定，仅图库分组且非点行引发时才记为设定状态。</summary>
    private void OnNavigationItemCollapsed(
        NavigationView sender,
        NavigationViewItemCollapsedEventArgs args) =>
        TrackGalleryExpansion(args.CollapsedItemContainer, false);

    /// <summary>记录本次按下是否落在图库行右侧的展开箭头区，供展开切换判定来源。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnGalleryPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(GalleryNavItem).Position;
        _isGalleryChevronClick = GalleryNavItem.ActualWidth - position.X <= GalleryChevronHitWidth;
    }

    /// <summary>记录图库分组的展开/折叠；点行本身引发的切换当场撤销，只保留箭头设定的状态。</summary>
    /// <param name="container">发生展开/折叠的项容器。</param>
    /// <param name="expanded">true 为展开，false 为折叠。</param>
    private void TrackGalleryExpansion(object? container, bool expanded)
    {
        if (_isSyncingGalleryExpansion || !ReferenceEquals(container, GalleryNavItem))
        {
            return;
        }

        // 点行引发的切换（标记由 ItemInvoked 置起，切换可能在其前也可能在其后）：
        // 立刻撤销回切换前的状态；点箭头引发的切换走下面的记录分支。
        if (_isGalleryContentClick && !_isGalleryChevronClick)
        {
            _isGalleryContentClick = false;
            RestoreGalleryExpansion(!expanded);
            return;
        }

        _isGalleryExpandedBeforeToggle = _isGalleryExpanded;
        _isGalleryExpanded = expanded;
    }

    /// <summary>
    /// 点击图库行本身只导航到图库：控件默认会把「点内容」也当作展开/折叠切换，
    /// 这里把切换撤销回原状态。
    /// </summary>
    /// <param name="sender">导航控件。</param>
    /// <param name="args">调用参数，含被点击项的容器。</param>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!ReferenceEquals(args.InvokedItemContainer, GalleryNavItem) || _isGalleryChevronClick)
        {
            return;
        }

        // 切换可能已经发生，也可能紧随其后，两条路径都要覆盖：先按当前状态还原一次，
        // 再把标记留到切换回调中消费；标记在下一个消息清除，避免污染后续交互。
        _isGalleryContentClick = true;
        RestoreGalleryExpansion(_isGalleryExpandedBeforeToggle);

        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
            () => _isGalleryContentClick = false);
    }

    /// <summary>把图库分组的展开状态改回指定值；状态已是该值时不重算子项。</summary>
    /// <param name="expanded">true 为展开，false 为折叠。</param>
    private void RestoreGalleryExpansion(bool expanded)
    {
        if (GalleryNavItem.IsExpanded == expanded)
        {
            return;
        }

        // 关键约束：改写必须延到下一个消息。展开/折叠是控件处理点击时同步推进的，
        // 在回调内改 IsExpanded 会重入其展开逻辑并使进程 fail-fast 退出（无托管异常、无日志）。
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            _isSyncingGalleryExpansion = true;
            _isGalleryExpanded = expanded;
            GalleryNavItem.IsExpanded = expanded;
            _isSyncingGalleryExpansion = false;
        });
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        // 左栏动态子项：按文件夹或分类过滤图库，导航上下文保持图库语义。
        if (tag.StartsWith(MediaFolderTagPrefix, StringComparison.Ordinal))
        {
            if (long.TryParse(tag.AsSpan(MediaFolderTagPrefix.Length), out var folderId))
            {
                _ = SelectMediaFolderAsync(folderId);
            }

            return;
        }

        if (tag.StartsWith(CategoryTagPrefix, StringComparison.Ordinal))
        {
            if (long.TryParse(tag.AsSpan(CategoryTagPrefix.Length), out var categoryId))
            {
                SelectCategory(categoryId);
            }

            return;
        }

        if (!Enum.TryParse<NavigationTarget>(tag, out var target))
        {
            return;
        }

        NavigateToTarget(target);
    }

    /// <summary>切换到指定导航目标：关闭查看器、重置分类过滤、装载目标页并触发目标专属初始化。</summary>
    /// <param name="target">目标页面。</param>
    private void NavigateToTarget(NavigationTarget target)
    {
        // 切换导航时必须关闭查看器，否则会停留在查看状态却显示导航页。
        CloseViewerIfVisible();

        _activeCategoryFilter = null;
        _currentTarget = target;
        NotifyTargetChanged();
        ApplyCurrentPage(target);
        OnPropertyChanged(nameof(SearchPlaceholder));

        // Videos 不在此下发筛选：该目标已由 ShortPage 承载，向图库视图模型下发
        // “仅视频”只会让结果落在当前不可见的页面上，且回到图库时还要再重置一次。
        switch (target)
        {
            case NavigationTarget.AllPhotos:
                _ = _gallery.ApplyNavigationFilterAsync(null, onlyFavorites: false);
                break;

            case NavigationTarget.Favorites:
                _ = _gallery.ApplyNavigationFilterAsync(null, onlyFavorites: true);
                break;

            case NavigationTarget.Settings:
                _ = _settingsPage.InitializeAsync();
                break;
        }
    }

    /// <summary>选中图库文件夹子项：切到图库页并按该文件夹过滤；可选从第一项开始幻灯片放映。</summary>
    private async Task SelectMediaFolderAsync(long folderId, bool startSlideShow = false)
    {
        var folder = _settings.Folders.FirstOrDefault(f => f.Folder.Id == folderId);

        if (folder is null)
        {
            return;
        }

        CloseViewerIfVisible();
        _activeCategoryFilter = null;
        _currentTarget = NavigationTarget.AllPhotos;
        NotifyTargetChanged();
        ShowPage(_galleryPage);
        OnPropertyChanged(nameof(SearchPlaceholder));

        await _gallery.ApplyMediaFolderFilterAsync(folder.Path, folder.DisplayName);

        if (!startSlideShow)
        {
            return;
        }

        // 过滤完成后第一页数据已就绪，直接以图库当前列表为播放列表打开放映窗口。
        var first = _gallery.Items.FirstOrDefault();

        if (first is null)
        {
            await ShowInfoDialogAsync("该文件夹暂无可放映的媒体。");
            return;
        }

        await OpenSlideShowAsync(_gallery.Items, first);
    }

    /// <summary>选中分类子项：切到图库页并按该分类过滤。</summary>
    private void SelectCategory(long categoryId)
    {
        var category = _categories.Categories.FirstOrDefault(c => c.Id == categoryId);

        if (category is null)
        {
            return;
        }

        CloseViewerIfVisible();
        _activeCategoryFilter = categoryId;
        _currentTarget = NavigationTarget.AllPhotos;
        NotifyTargetChanged();
        ShowPage(_galleryPage);
        OnPropertyChanged(nameof(SearchPlaceholder));
        _ = _gallery.ApplyCategoryFilterAsync(categoryId, category.Name);
    }

    /// <summary>切换导航时必须关闭查看器，否则会停留在查看状态却显示导航页。</summary>
    private void CloseViewerIfVisible()
    {
        if (!IsViewerVisible)
        {
            return;
        }

        // 必须在播放态容器仍可见时卸载页面：Collapsed 容器不会触发 Unloaded，
        // MediaPlayer 就得不到释放（解码器不回收，反复进出播放会内存持续增长）。
        DetachVideoPage();

        IsViewerVisible = false;
        ExitFullScreenIfNeeded();
        OnChromeVisibilityChanged();
    }

    /// <summary>退出系统全屏，还原查看器打开前的窗口形态；未全屏时跳过（视频播放器路径）。</summary>
    private void ExitFullScreenIfNeeded()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            ToggleFullScreen();
        }
    }

    /// <summary>把页面装载到内容宿主；内容已是目标页时跳过，避免重复挂载触发整页重建（切换文件夹卡顿的成因之一）。</summary>
    private void ShowPage(Page page)
    {
        if (!ReferenceEquals(PageHost.Content, page))
        {
            PageHost.Content = page;
        }
    }

    /// <summary>把当前导航目标对应的页面实例装载到内容宿主。</summary>
    private void ApplyCurrentPage(NavigationTarget target)
    {
        switch (target)
        {
            case NavigationTarget.Settings:
                ShowPage(_settingsPage);
                break;

            case NavigationTarget.Videos:
                ShowPage(_shortPage);
                break;

            case NavigationTarget.Favorites:
            case NavigationTarget.AllPhotos:
                ShowPage(_galleryPage);
                break;
        }
    }

    /// <summary>标题栏「设置」按钮：跳转设置页。</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e) => NavigateToSettings();

    /// <summary>跳转设置页：设置页没有左栏导航项，直接切换目标；已在该页时忽略。</summary>
    private void NavigateToSettings()
    {
        if (_currentTarget is NavigationTarget.Settings)
        {
            return;
        }

        // 必须清掉左栏选中态：否则高亮仍停留在上一项，用户再点该项时 SelectedItem 未变化、
        // 不会触发导航，就再也回不到图库。
        NavigationViewControl.SelectedItem = null;
        NavigateToTarget(NavigationTarget.Settings);
    }

    /// <summary>幻灯片菜单项字形，与图库页工具栏按钮同源（SlideShowGlyph）。</summary>
    private const string SlideShowGlyph = "\uE786";

    /// <summary>空心文件夹字形：Segoe Fluent Icons 的 E8B7 是实心 FolderFill，ED25 在两代字体下均为空心斜开盖文件夹，左栏子项与图库页头共用。</summary>
    private const string FolderGlyph = "\uED25";

    /// <summary>分类子项字形：ED41 为带角标的实心文件夹，与图库子项的空心文件夹区分开。</summary>
    private const string CategoryFolderGlyph = "\uED41";

    /// <summary>在文件资源管理器中打开的菜单项名，扫描期间唯一保持可用的项（只读浏览）。</summary>
    private const string OpenInExplorerItemName = "MenuOpenInExplorer";

    /// <summary>创建文件夹子项的右键菜单：创建 / 重命名 / 放映 / 打开 + 移除 / 删除。</summary>
    /// <param name="folder">菜单操作的目标扫描源。</param>
    /// <returns>独立构建的菜单实例；可重复弹出的菜单必须每次新建，共享实例会抛异常。</returns>
    private MenuFlyout CreateFolderContextMenu(LibraryFolderRow folder)
    {
        var menu = new MenuFlyout();
        menu.Opening += OnFolderMenuOpening;

        menu.Items.Add(CreateFolderMenuItem(
            "创建文件夹", new FontIcon { Glyph = "\uE8F4" }, folder, OnCreateSubFolderClick));
        menu.Items.Add(CreateFolderMenuItem(
            "重命名", new SymbolIcon(Symbol.Rename), folder, OnRenameFolderClick));
        menu.Items.Add(CreateFolderMenuItem(
            "开始幻灯片放映", new FontIcon { Glyph = SlideShowGlyph }, folder, OnSlideShowFolderClick));
        menu.Items.Add(CreateFolderMenuItem(
            "在文件资源管理器中打开", new SymbolIcon(Symbol.OpenLocal), folder, OnOpenFolderInExplorerClick,
            OpenInExplorerItemName));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateFolderMenuItem(
            "从图库中移除文件夹", new FontIcon { Glyph = "\uECC9" }, folder, OnRemoveMediaFolderClick));

        // 删除项：前景取统一的删除色（浅色 #C42B1C / 深色 #FF99A4，随主题从 ThemeDictionaries 取键），
        // 并通过项级主题键覆盖 hover/pressed 保持红色（与图库图片菜单一致），不重写 ControlTemplate（避免触发旋转忙碌光标）。
        var deleteBrush = (SolidColorBrush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[
            (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark ? "Dark" : "Default"])["PixbianDeleteForeground"];
        var deleteItem = CreateFolderMenuItem(
            "删除文件夹", new SymbolIcon(Symbol.Delete), folder, OnDeleteFolderClick);
        deleteItem.Foreground = deleteBrush;
        deleteItem.Resources["MenuFlyoutItemForegroundPointerOver"] = deleteBrush;
        deleteItem.Resources["MenuFlyoutItemForegroundPressed"] = deleteBrush;
        menu.Items.Add(deleteItem);

        return menu;
    }

    /// <summary>构建单个文件夹菜单项；目标扫描源经 Tag 传递给 Click 处理器。</summary>
    private static MenuFlyoutItem CreateFolderMenuItem(
        string text,
        IconElement icon,
        LibraryFolderRow folder,
        RoutedEventHandler onClick,
        string? name = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon,
            Tag = folder
        };

        if (name is not null)
        {
            item.Name = name;
        }

        item.Click += onClick;
        return item;
    }

    /// <summary>菜单打开时按索引状态刷新可用性：扫描期间除只读浏览外全部禁用，避免磁盘与索引操作并发。</summary>
    private void OnFolderMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu)
        {
            return;
        }

        foreach (var entry in menu.Items)
        {
            if (entry is MenuFlyoutItem { Tag: LibraryFolderRow } item
                && item.Name != OpenInExplorerItemName)
            {
                item.IsEnabled = !_settings.IsIndexing;
            }
        }
    }

    /// <summary>右键菜单「创建文件夹」：输入名称后在目标文件夹下创建子文件夹。</summary>
    private async void OnCreateSubFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryFolderRow row })
        {
            return;
        }

        var name = await ShowFolderNameDialogAsync("创建文件夹", "文件夹名称", string.Empty);

        if (name is not null)
        {
            await _settings.CreateSubFolderCommand.ExecuteAsync((row, name));
        }
    }

    /// <summary>右键菜单「重命名」：输入新名后磁盘改名并迁移图库索引。</summary>
    private async void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryFolderRow row })
        {
            return;
        }

        var name = await ShowFolderNameDialogAsync("重命名", "新名称", row.DisplayName);

        if (name is not null)
        {
            await _settings.RenameFolderCommand.ExecuteAsync((row, name));
        }
    }

    /// <summary>右键菜单「开始幻灯片放映」：切到该文件夹并从第一项开始放映。</summary>
    private async void OnSlideShowFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryFolderRow row })
        {
            return;
        }

        await SelectMediaFolderAsync(row.Folder.Id, startSlideShow: true);
    }

    /// <summary>右键菜单「在文件资源管理器中打开」：用系统资源管理器打开该文件夹。</summary>
    private async void OnOpenFolderInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryFolderRow row })
        {
            return;
        }

        if (!Directory.Exists(row.Path))
        {
            await ShowInfoDialogAsync("文件夹不存在或已被移动。");
            return;
        }

        // 路径来自受信任的索引数据，经 argv 形式传入并引号包裹，杜绝命令注入。
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{row.Path}\"")
        {
            UseShellExecute = true
        });
    }

    /// <summary>右键菜单「删除文件夹」：二次确认后整个文件夹移入回收站，并清理图库索引。</summary>
    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryFolderRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除文件夹",
            Content = $"将从图库移除该文件夹，并把整个文件夹（含全部文件）移入系统回收站。\n\n{row.Path}",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _settings.DeleteFolderCommand.ExecuteAsync(row);

        // 若正浏览该文件夹，子项重建会回落图库根并经导航事件清空过滤；
        // 浏览图库根或其他视图时不会触发导航，此处统一刷新兜底（重复刷新无害）。
        await _gallery.ReloadCommand.ExecuteAsync(null);
    }

    /// <summary>弹出文件夹名称输入对话框；返回 null 表示取消，否则为通过校验的名称。</summary>
    private async Task<string?> ShowFolderNameDialogAsync(string title, string placeholder, string initialText)
    {
        var input = new TextBox
        {
            Text = initialText,
            PlaceholderText = placeholder
        };

        // WinUI 3 的 TextBox 无 SelectAllOnFocus 属性，改为获得焦点时全选，便于用户直接覆盖输入。
        input.Loaded += (_, _) => input.SelectAll();

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = input,
            XamlRoot = RootGrid.XamlRoot
        };

        dialog.IsPrimaryButtonEnabled = IsValidFolderName(initialText);
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidFolderName(input.Text);

        return await dialog.ShowAsync() == ContentDialogResult.Primary && IsValidFolderName(input.Text)
            ? input.Text.Trim()
            : null;
    }

    /// <summary>名称非空且不含文件系统非法字符时方可确认。</summary>
    private static bool IsValidFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>弹出轻量提示对话框。</summary>
    private async Task ShowInfoDialogAsync(string message)
    {
        await new ContentDialog
        {
            Title = "提示",
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot
        }.ShowAsync();
    }

    /// <summary>右键菜单「移除」：二次确认后移除扫描源并清理其索引记录。</summary>
    private async void OnRemoveMediaFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryFolderRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "移除扫描源",
            Content = $"将同时清理该目录下的索引记录（不会删除磁盘文件）。\n\n{row.Path}",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _settings.RemoveFolderCommand.ExecuteAsync(row);

        // 被移除文件夹的索引条目已清理：正浏览该文件夹时，子项重建会回落图库根并经导航事件
        // 自动清空过滤；浏览图库根或其他视图时不会触发导航，此处统一刷新兜底（重复刷新无害）。
        await _gallery.ReloadCommand.ExecuteAsync(null);
    }

    /// <summary>左栏「图库」右键菜单 / Ctrl+I：选取文件夹后加入媒体库并自动索引，完成后刷新图库。</summary>
    private async Task AddMediaFolderAsync()
    {
        // 与设置页同一选择器方案：Windows App SDK 的 Picker 原生支持非打包应用，
        // 构造传入 WindowId 即完成归属。
        var picker = new FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        var folder = await picker.PickSingleFolderAsync();

        if (folder is null)
        {
            return;
        }

        // AddFolderCommand 内部完成「添加 + 新源索引」，期间重复点击不会叠加索引任务。
        await _settings.AddFolderCommand.ExecuteAsync(folder.Path);

        // 当前正处于图库上下文，主动刷新让新增媒体立即可见；
        // 刷新失败不影响已完成的添加与索引结果。
        try
        {
            await _gallery.ReloadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Reload gallery after indexing failed: {ex}");
        }
    }

    /// <summary>右键菜单入口。</summary>
    private void OnAddMediaFolderClick(object sender, RoutedEventArgs e) => _ = AddMediaFolderAsync();

    /// <summary>窗口级快捷键：Ctrl+I 打开添加媒体文件夹的选择器。</summary>
    private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.I
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                is not (CoreVirtualKeyStates.Down or CoreVirtualKeyStates.Locked))
        {
            return;
        }

        // 焦点在文本编辑框内时放行，避免输入时误触发。
        if (FocusManager.GetFocusedElement() is TextBox)
        {
            return;
        }

        e.Handled = true;
        _ = AddMediaFolderAsync();
    }

    /// <summary>扫描源集合变化后重建图库分组子项。</summary>
    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshLibraryFolderItems();

    /// <summary>分类集合变化后重建分类分组子项。</summary>
    private void OnCategoriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshCategoryItems();

    /// <summary>按当前扫描源重建图库分组子项；被移除的文件夹若正被选中，回落到图库根视图。</summary>
    private void RefreshLibraryFolderItems()
    {
        var selectedTag = (NavigationViewControl.SelectedItem as NavigationViewItem)?.Tag as string;

        // 先收起再重建：NavigationView 把层级子项扁平进同一个列表，且只在 IsExpanded
        // 变化时重算，状态不变（哪怕子项是后加的）就不会把新子项插进列表。
        _isSyncingGalleryExpansion = true;
        GalleryNavItem.IsExpanded = false;

        GalleryNavItem.MenuItems.Clear();

        foreach (var folder in _settings.Folders)
        {
            var item = new NavigationViewItem
            {
                Content = folder.DisplayName,
                Icon = new FontIcon { Glyph = FolderGlyph },
                Tag = $"{MediaFolderTagPrefix}{folder.Folder.Id}"
            };
            item.ContextFlyout = CreateFolderContextMenu(folder);
            GalleryNavItem.MenuItems.Add(item);
        }

        GalleryNavItem.IsExpanded = _isGalleryExpanded;
        _isSyncingGalleryExpansion = false;

        if (selectedTag?.StartsWith(MediaFolderTagPrefix, StringComparison.Ordinal) == true
            && FindNavItem(NavigationViewControl.MenuItems, selectedTag) is null)
        {
            NavigationViewControl.SelectedItem = GalleryNavItem;
            return;
        }

        RestoreSelection(selectedTag);
    }

    /// <summary>按当前分类重建分类分组子项；被移除的分类若正被选中，回落到图库根视图。</summary>
    private void RefreshCategoryItems()
    {
        var selectedTag = (NavigationViewControl.SelectedItem as NavigationViewItem)?.Tag as string;

        // 先收起再重建：NavigationView 把层级子项扁平进同一个列表，且只在 IsExpanded
        // 变化时重算，状态不变（哪怕子项是后加的）就不会把新子项插进列表。
        var wasExpanded = CategoriesNavItem.IsExpanded;
        CategoriesNavItem.IsExpanded = false;

        CategoriesNavItem.MenuItems.Clear();

        foreach (var category in _categories.Categories)
        {
            CategoriesNavItem.MenuItems.Add(new NavigationViewItem
            {
                Content = category.Name,
                Icon = new FontIcon { Glyph = CategoryFolderGlyph },
                Tag = $"{CategoryTagPrefix}{category.Id}"
            });
        }

        CategoriesNavItem.IsExpanded = wasExpanded;

        if (selectedTag?.StartsWith(CategoryTagPrefix, StringComparison.Ordinal) == true
            && FindNavItem(NavigationViewControl.MenuItems, selectedTag) is null)
        {
            NavigationViewControl.SelectedItem = GalleryNavItem;
            return;
        }

        RestoreSelection(selectedTag);
    }

    /// <summary>子项重建后按 Tag 恢复选中，避免刷新列表打断当前浏览上下文。</summary>
    private void RestoreSelection(string? selectedTag)
    {
        if (selectedTag is null)
        {
            return;
        }

        var item = FindNavItem(NavigationViewControl.MenuItems, selectedTag);

        if (item is not null && !ReferenceEquals(item, NavigationViewControl.SelectedItem))
        {
            NavigationViewControl.SelectedItem = item;
        }
    }

    /// <summary>按 Tag 在导航项树中查找条目。</summary>
    /// <param name="items">待查找的导航项集合。</param>
    /// <param name="tag">目标标记。</param>
    /// <returns>匹配的导航项；未命中时返回 null。</returns>
    private static NavigationViewItem? FindNavItem(IEnumerable<object> items, string tag)
    {
        foreach (var entry in items)
        {
            if (entry is not NavigationViewItem container)
            {
                continue;
            }

            if (container.Tag as string == tag)
            {
                return container;
            }

            var nested = FindNavItem(container.MenuItems, tag);

            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        // 根布局加载后把焦点移到导航栏，避免搜索框一启动就获得焦点并显示输入光标。
        _ = NavigationViewControl.Focus(FocusState.Programmatic);
        UpdateTitleBarPassthrough();

        // 窗口在不同 DPI 的显示器之间移动时 XamlRoot 会变更缩放比，须持续跟进。
        RootGrid.XamlRoot.Changed += OnXamlRootChanged;
        SyncThumbnailScale();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SyncThumbnailScale();

    /// <summary>把当前显示缩放比同步给缩略图服务，使缩略图按物理像素解码；缩放比变化时重新加载已显示的缩略图。</summary>
    private void SyncThumbnailScale()
    {
        if (RootGrid.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var scale = xamlRoot.RasterizationScale;

        if (Math.Abs(scale - _lastRasterizationScale) < 0.01)
        {
            return;
        }

        // 首次同步时还没有已加载的缩略图，无需触发重新加载。
        var isFirstSync = _lastRasterizationScale == 0;
        _lastRasterizationScale = scale;
        _thumbnails.RasterizationScale = scale;

        if (!isFirstSync)
        {
            _ = _gallery.RefreshThumbnailsAsync();
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarPassthrough();

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e) => UpdateTitleBarPassthrough();

    /// <summary>
    /// 把标题栏交互控件的矩形注册为指针 Passthrough 区域。
    /// 矩形为物理像素（须按显示缩放换算），布局或窗口状态变化后必须重新注册。
    /// </summary>
    private void UpdateTitleBarPassthrough()
    {
        if (AppWindow is null || RootGrid.XamlRoot is null)
        {
            return;
        }

        var scale = RootGrid.XamlRoot.RasterizationScale;
        var rects = new List<RectInt32>();

        // 播放态下标题栏行由播放器页接管：放行其顶栏内的交互控件（返回按钮）；
        // 常态放行搜索框与设置按钮。被隐藏的一侧 ActualWidth 为 0、会被下方跳过，
        // 故按状态二选一即可，无需合并集合。
        IEnumerable<FrameworkElement> elements = IsViewerVisible
            && VideoHost.Content is VideoPlayerPage videoPage
                ? videoPage.TitleBarInteractiveElements
                : [SearchBox, SettingsButton];

        foreach (var element in elements)
        {
            if (element is not { IsLoaded: true, ActualWidth: > 0 } framework)
            {
                continue;
            }

            var bounds = framework.TransformToVisual(null)
                .TransformBounds(new Rect(default, new Size(framework.ActualWidth, framework.ActualHeight)));

            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    private async void OnSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        await _shell.OnSearchTextChangedAsync(sender.Text);
    }

    private async void OnSearchSubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        await _gallery.ApplySearchAsync(sender.Text);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        ApplySettings(settings);
    }

    private void ApplySettings(AppSettings settings)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = MapTheme(settings.Theme);

            // 系统标题栏按钮不随应用主题变化，须在主题切换后显式刷新一次。
            UpdateCaptionButtonColors();

            // 按钮悬停 / 按下底色随主题切换刷新（主题字典覆盖在浅色下不生效，见 App）。
            App.ApplyButtonHoverBrushes(root.ActualTheme == ElementTheme.Dark);
        }

        // SetThumbnailSizeAsync 的同步段先更新尺寸值，随后的 ApplyThumbnailSize
        // 触发的属性通知才能读到新值，顺序不可颠倒。
        _ = _gallery.SetThumbnailSizeAsync(settings.ThumbnailSize);
        _galleryPage.ApplyViewMode(settings.ViewMode);
        _galleryPage.ApplyThumbnailSize();

        // 幻灯片间隔、播放顺序与切换方式：查看器不反向依赖设置服务，由外壳在设置变更时推送，
        // 放映途中改设置也能即时生效（ApplySettings 内部会保留播放状态续跑定时器）。
        _viewer.ApplySettings(settings);

        // 放映窗口打开期间（含放映内选项 Flyout 写回）同样须把设置推给放映视图模型；
        // 窗口未打开时无接收方，跳过（打开放映前 OpenSlideShowAsync 会先推送一次）。
        _slideShowWindow?.Page.ViewModel.ApplySettings(settings);

        // 片段区间档位：短片页同样不反向依赖设置服务，与幻灯片共用 ClipRangePlanner 规则。
        _shortPage.ViewModel.ApplySettings(settings);

        NotifyTargetChanged();
    }

    /// <summary>在图库中双击条目时打开查看器：图片走图片查看器，视频走播放器。</summary>
    /// <param name="item">被双击的条目。</param>
    public async Task OpenViewerAsync(MediaItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var items = _gallery.Items.Select(i => i.Item).ToList();

        if (item.Item.Kind == MediaKind.Video)
        {
            // 播放队列取图库当前列表中的视频子集：上/下一条在当前筛选结果内连续切换。
            var videos = items.Where(i => i.Kind == MediaKind.Video).ToList();
            var videoIndex = Math.Max(0, videos.FindIndex(i => i.Id == item.Id));
            await OpenVideoPlayerAsync(videos, videoIndex);
            return;
        }

        if (item.Item.Kind != MediaKind.Image)
        {
            return;
        }

        var index = items.FindIndex(i => i.Id == item.Id);

        // 查看器以独立全屏窗口打开，主窗口保持原样；已有未关闭的查看器窗口时直接复用
        // （重载播放列表并带到前台），避免叠加多个全屏窗口。
        var viewerWindow = _imageViewerWindow;

        if (viewerWindow is null)
        {
            viewerWindow = App.Services.GetRequiredService<ImageViewerWindow>();
            viewerWindow.Closed += (_, _) => _imageViewerWindow = null;
            _imageViewerWindow = viewerWindow;
        }

        viewerWindow.Activate();
        viewerWindow.ViewerPage.BeginOpen();

        await _viewer.LoadPlaylistAsync(items, Math.Max(0, index));
    }

    /// <summary>打开幻灯片放映窗口：以图库当前列表为候选（是否含视频按设置过滤），从指定条目起播。</summary>
    /// <param name="candidates">放映候选列表。</param>
    /// <param name="start">起始条目。</param>
    /// <remarks>
    /// 已有未关闭的放映窗口时直接复用（重载列表并带到前台），避免叠加多个全屏窗口；
    /// 放映配置经放映视图模型的 ApplySettings 推送，与设置页变更保持同一链路。
    /// </remarks>
    public async Task OpenSlideShowAsync(IReadOnlyList<MediaItemViewModel> candidates, MediaItemViewModel start)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(start);

        var items = candidates.Select(i => i.Item).ToList();
        var settings = _settings.Settings;

        var window = _slideShowWindow;

        if (window is null)
        {
            window = App.Services.GetRequiredService<SlideShowWindow>();
            window.Closed += (_, _) => _slideShowWindow = null;
            _slideShowWindow = window;
        }

        window.Activate();

        var viewModel = window.Page.ViewModel;

        // 设置必须先于 BeginOpen：页面的背景层初值取自视图模型当前的虚化开关，
        // 顺序颠倒会让首次打开的舞台沿用上一轮（或默认）的虚化状态。
        viewModel.ApplySettings(settings);
        window.Page.BeginOpen();

        await viewModel.LoadPlaylistAsync(items, start.Item);
        viewModel.StartCommand.Execute(null);
    }

    /// <summary>查看器请求移交放映：以查看器当前列表与条目开放映窗口，随后关闭查看器。</summary>
    private async void OnViewerSlideShowHandoffRequested(object? sender, EventArgs e)
    {
        var viewerWindow = _imageViewerWindow;

        if (viewerWindow is null)
        {
            return;
        }

        var currentItem = viewerWindow.ViewerPage.ViewModel.CurrentItem;
        var candidates = _gallery.Items.ToList();

        // 先关查看器再开放映：避免两个全屏置顶窗口短暂叠加争焦点。
        viewerWindow.Close();

        var start = candidates.FirstOrDefault(i => currentItem is not null && i.Id == currentItem.Id)
            ?? candidates.FirstOrDefault(i => !i.IsVideo);

        if (start is null)
        {
            return;
        }

        await OpenSlideShowAsync(candidates, start);
    }

    /// <summary>按领域模型打开视频播放器；供短片页「查看原视频」等非图库入口使用。</summary>
    /// <param name="item">媒体条目；非视频类型直接返回。</param>
    /// <remarks>与图库双击共用同一条装载链路，避免两套代码在「播放态切换」上各自漂移。</remarks>
    public async Task OpenViewerAsync(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Kind == MediaKind.Video)
        {
            await OpenVideoPlayerAsync([item], 0);
        }
    }

    /// <summary>把播放器页装进播放态宿主并按队列起播指定视频。</summary>
    /// <param name="items">视频队列（调用方过滤掉非视频条目）。</param>
    /// <param name="startIndex">起始索引。</param>
    private async Task OpenVideoPlayerAsync(IReadOnlyList<MediaItem> items, int startIndex)
    {
        // 播放器页延迟解析：其 MediaPlayerElement 在应用启动阶段构造会触发 WinRT 异常。
        var videoPage = App.Services.GetRequiredService<VideoPlayerPage>();

        // 顺序不可调换：先显示播放态根，再把页面装进 VideoHost——
        // 往 Collapsed 的容器里塞内容不会触发 Loaded，页面初始化（含顶栏布局）会被整段跳过。
        IsViewerVisible = true;
        OnChromeVisibilityChanged();
        AttachVideoPage(videoPage);

        await videoPage.OpenAsync(items, startIndex);
    }

    /// <summary>当前是否处于全屏演示态（Esc 分级退出需要先判断）。</summary>
    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>切换全屏状态。</summary>
    public void ToggleFullScreen()
    {
        // AppWindowPresenter.Kind 是只读的，切换演示器必须使用 SetPresenter。
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        // 播放器页需感知全屏切换：全屏时隐藏底栏、全屏按钮切换为「返回窗口」。
        if (VideoHost.Content is VideoPlayerPage videoPage)
        {
            videoPage.OnWindowFullScreenChanged(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        }
    }

    /// <summary>播放态切换的收口：通知导航栏收起，并切换窗口级分支。</summary>
    private void OnChromeVisibilityChanged()
    {
        OnPropertyChanged(nameof(NavigationPaneMode));
        ApplyViewerChrome();
    }

    /// <summary>在「常态外壳」与「播放态独立根」之间切换，两者结构互不影响。</summary>
    /// <remarks>
    /// 播放态隐藏标题栏行与导航栏，只显示 PlayerRoot（覆盖标题栏行与内容行），
    /// 画面顶到窗口最上沿，顶栏由播放器页自绘并与系统窗口按钮同排；常态反向切回。
    /// 内容卡片的外观属性全程不被改写，故无需在运行期覆盖与还原。
    /// </remarks>
    private void ApplyViewerChrome()
    {
        var isVideo = IsViewerVisible;

        TitleBar.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        NavigationViewControl.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        PlayerRoot.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;

        // 播放态背景被 PlayerRoot 完全遮挡：一并隐藏，避免背景层继续参与每帧合成。
        WallpaperLayer.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

        // 播放态舞台恒为暗色，系统按钮配色须随分支切换重算（浅色主题下不能沿用黑字）。
        UpdateCaptionButtonColors();

        SchedulePassthroughRefresh();
    }

    /// <summary>把播放器页装载到播放态宿主。</summary>
    /// <param name="videoPage">播放器页（依赖注入单例）。</param>
    /// <remarks>
    /// 播放器页只能在 VideoHost 这一处；重复装载同一实例直接跳过，避免 Content 反复变更
    /// 触发无谓的 Unloaded/Loaded。装载后立即排一帧刷新 Passthrough：调用链上
    /// ApplyViewerChrome 的刷新排在装载之前，那一拍读到的 Content 还是空。
    /// 左上角常驻返回按钮落在系统标题栏区域内，必须经 Passthrough 放行才能收到点击。
    /// </remarks>
    private void AttachVideoPage(VideoPlayerPage videoPage)
    {
        if (!ReferenceEquals(VideoHost.Content, videoPage))
        {
            VideoHost.Content = videoPage;
        }

        SchedulePassthroughRefresh();
    }

    /// <summary>卸载播放器页：置空 Content 触发其 Unloaded，进而释放 MediaPlayer。</summary>
    /// <remarks>
    /// 仅把 PlayerRoot 切为 Collapsed 不会触发 Unloaded，解码器就得不到回收，
    /// 反复进出播放会导致内存持续增长。
    /// </remarks>
    private void DetachVideoPage()
    {
        VideoHost.Content = null;
    }

    /// <summary>排到下一帧刷新标题栏放行区域：可见性刚变时布局尚未重算，立即取矩形会拿到旧值。</summary>
    private void SchedulePassthroughRefresh() =>
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            UpdateTitleBarPassthrough);

    /// <summary>关闭视频播放器，返回图库（图片查看器为独立窗口，经其自身 Close 关闭）。</summary>
    public void CloseViewer()
    {
        // 卸载播放器页即触发其 Unloaded，进而释放 MediaPlayer；须在容器仍可见时执行。
        // 先退全屏再卸载，避免全屏演示器切换与视觉树变更在同一帧叠加。
        ExitFullScreenIfNeeded();
        DetachVideoPage();

        IsViewerVisible = false;
        OnChromeVisibilityChanged();

        ApplyCurrentPage(_currentTarget);
    }

    /// <summary>把领域层的主题枚举映射为 WinUI 的主题枚举。</summary>
    private static ElementTheme MapTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>跟随系统主题时，系统深浅反转同步刷新标题栏按钮颜色（背景渐变经 ThemeResource 随主题自动切换）。</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtonColors();

        // 按钮悬停 / 按下底色随主题反转同步刷新。
        App.ApplyButtonHoverBrushes(sender.ActualTheme == ElementTheme.Dark);
    }

    /// <summary>按实际生效主题刷新系统标题栏按钮（最小化/最大化/关闭）颜色。</summary>
    /// <remarks>
    /// ExtendsContentIntoTitleBar 开启后，系统按钮前景色不再随应用主题更新，必须显式赋值；
    /// 按钮底色一律转透明以融入标题栏行（该行为透明，背景图由根布局底层透出）。
    /// 悬停/按下底色也必须显式赋值：不设时回落到系统默认高亮（暗色下约 20% 白），
    /// 视觉上远重于 Fluent 的 Subtle 反馈；此处取值与 SubtleFillColorSecondary/Tertiary
    /// 两个主题令牌一致（暗 8%/6% 白、浅 6%/4% 黑），与设置按钮的悬停观感对齐。
    /// 跟随系统时 ActualTheme 由系统决定，故此处读实际主题而非设置值。
    /// 播放态恒取白色系：舞台与顶栏底恒为暗色，与主题无关——浅色主题下若按主题取黑，
    /// 按钮会变成黑字压黑底而不可见。
    /// </remarks>
    private void UpdateCaptionButtonColors()
    {
        if (AppWindow?.TitleBar is not { } titleBar)
        {
            return;
        }

        var useLightForeground = IsViewerVisible
            || (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var foreground = useLightForeground ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;

        // 悬停/按下高亮取 Subtle 令牌同值：AppliesToBorder 画刷无法直接用于 AppWindow（要 Color），
        // 故按主题分别给出与令牌等值的 ARGB。
        var hoverBackground = useLightForeground
            ? Microsoft.UI.ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var pressedBackground = useLightForeground
            ? Microsoft.UI.ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(0x0A, 0x00, 0x00, 0x00);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private void NotifyTargetChanged()
    {
        OnPropertyChanged(nameof(IsGalleryVisible));
        OnPropertyChanged(nameof(IsVideosVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
