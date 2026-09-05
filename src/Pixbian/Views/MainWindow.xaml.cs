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

    private readonly ShellViewModel _shell;
    private readonly GalleryViewModel _gallery;
    private readonly SettingsViewModel _settings;
    private readonly ImageViewerViewModel _viewer;
    private readonly CategoryViewModel _categories;
    private readonly IThumbnailService _thumbnails;
    private readonly GalleryPage _galleryPage;
    private readonly SettingsPage _settingsPage;

    /// <summary>当前打开的图片查看器窗口；窗口关闭（Closed）后置 null，下次打开创建新实例。</summary>
    private ImageViewerWindow? _imageViewerWindow;

    /// <summary>【临时诊断】UI 线程心跳定时器：必须持字段强引用，否则构造函数结束后即被 GC 回收、心跳静默停止。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _heartbeat;

    private NavigationTarget _currentTarget = NavigationTarget.AllPhotos;
    private bool _isViewerVisible;
    private double _lastRasterizationScale;

    /// <summary>当前按分类过滤的主键；刷新分类完成后据此重放过滤，非分类过滤上下文为 null。</summary>
    private long? _activeCategoryFilter;

    /// <summary>初始化主窗口。</summary>
    /// <param name="shell">外壳视图模型。</param>
    /// <param name="gallery">图库视图模型。</param>
    /// <param name="settings">设置视图模型。</param>
    /// <param name="viewer">图片查看器视图模型。</param>
    /// <param name="categories">分类视图模型，驱动左栏分类子项。</param>
    /// <param name="thumbnails">缩略图服务，用于同步显示缩放比。</param>
    /// <param name="galleryPage">图库页实例。</param>
    /// <param name="settingsPage">设置页实例。</param>
    public MainWindow(
        ShellViewModel shell,
        GalleryViewModel gallery,
        SettingsViewModel settings,
        ImageViewerViewModel viewer,
        CategoryViewModel categories,
        IThumbnailService thumbnails,
        GalleryPage galleryPage,
        SettingsPage settingsPage)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(gallery);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(galleryPage);
        ArgumentNullException.ThrowIfNull(settingsPage);

        _shell = shell;
        _gallery = gallery;
        _settings = settings;
        _viewer = viewer;
        _categories = categories;
        _thumbnails = thumbnails;
        _galleryPage = galleryPage;
        _settingsPage = settingsPage;

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
        // unpackaged 应用的 PRI 不索引 Content 项，ms-appx:// 形式的 URI 解析不到该文件。
        // 限定解码宽度，避免 2MB 的原图按原始分辨率解码后长期占用显存。
        var wallpaperPath = Path.Combine(AppContext.BaseDirectory, "Assets", "light.jpg");
        if (File.Exists(wallpaperPath))
        {
            WallpaperImage.Source = new BitmapImage(new Uri(wallpaperPath))
            {
                DecodePixelWidth = 2560,
            };
        }

        // Window 不继承 FrameworkElement，没有 DataContext，故设置在根元素上。
        // AppWindow 为 WinUI 3 的 Window 内置属性，无需另行获取。
        RootGrid.DataContext = this;

        // 图库页需要回调主窗口以打开查看器；设置页需要主窗口的 WindowId 归属文件夹选择器。
        _galleryPage.Owner = this;
        _settingsPage.Owner = this;

        _shell.SettingsChanged += OnSettingsChanged;

        // 图库查询状态经代理属性转发给窗口层 loading 覆盖层；
        // 覆盖层挂在窗口层（PageHost 兄弟位），与图库页内部布局解耦以规避布局循环。
        _gallery.PropertyChanged += OnGalleryPropertyChanged;
        SyncLoadingOverlay();

        // 快捷键经根网格代码后置处理：在根容器注册 KeyboardAccelerator 会让全窗口
        // 所有 ToolTip 追加速度提示（官方行为且无法关闭），故只能走 KeyDown 分支。
        RootGrid.KeyDown += OnRootGridKeyDown;

        // 左栏动态子项由两个视图模型的集合驱动：设置页增删扫描源、分类页增删分类后自动同步。
        _settings.Folders.CollectionChanged += OnFoldersChanged;
        _categories.Categories.CollectionChanged += OnCategoriesChanged;

        // 启动默认进入收藏夹：按 Tag 定位，避免依赖菜单项的排列顺序。
        NavigationViewControl.SelectedItem =
            FindNavItem(NavigationViewControl.MenuItems, "Favorites") ?? NavigationViewControl.MenuItems[0];
        ApplySettings(_shell.Settings);

        _ = InitializeAsync();

        // 【临时诊断】UI 线程心跳：定时器回调在 UI 线程执行，只要日志持续出现 TICK 就说明
        // UI 线程与消息循环存活。画面冻结而 TICK 继续 → 渲染/合成停摆；
        // TICK 一并停止 → UI 线程被同步等待或异常挂住。这是二者的唯一判别依据。
        _heartbeat = DispatcherQueue.CreateTimer();
        _heartbeat.Interval = TimeSpan.FromMilliseconds(500);
        _heartbeat.Tick += (_, _) =>
            Pixbian.Services.Diagnostics.Log($"TICK|{Environment.TickCount64}");
        _heartbeat.Start();
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

    /// <summary>图库查询进行中：驱动窗口层 loading 覆盖层的装载与卸载。</summary>
    public bool IsGalleryQuerying => _gallery.IsQuerying;

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



    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        // 【临时诊断】导航点击留痕：复现「点击无效」时，此日志缺失即证明点击未到达
        // UI 事件层（输入路由被吞），到达而无后续 LOAD 则是加载链路挂起。
        Pixbian.Services.Diagnostics.Log("NAVCLICK");

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

        switch (target)
        {
            case NavigationTarget.AllPhotos:
                _ = _gallery.ApplyNavigationFilterAsync(null, onlyFavorites: false);
                break;

            case NavigationTarget.Videos:
                _ = _gallery.ApplyNavigationFilterAsync(MediaKind.Video, onlyFavorites: false);
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

        // 过滤完成后第一页数据已就绪，直接以图库当前列表为播放列表打开查看器。
        var first = _gallery.Items.FirstOrDefault();

        if (first is null)
        {
            await ShowInfoDialogAsync("该文件夹暂无可放映的媒体。");
            return;
        }

        await OpenViewerAsync(first, startSlideShow: true);
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

        _viewer.StopSlideShow();
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
            "从图库中移除文件夹", new SymbolIcon(Symbol.Remove), folder, OnRemoveMediaFolderClick));

        // 删除项：前景固定红色，并通过项级主题键覆盖 hover/pressed 保持红色（与图库图片菜单一致），
        // 不重写 ControlTemplate（避免触发旋转忙碌光标）。
        var deleteBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
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

    /// <summary>左栏「刷新分类」按钮：对全库批量重新匹配分类规则，期间禁用按钮防重入。</summary>
    private async void OnRefreshCategoriesClick(object sender, RoutedEventArgs e)
    {
        if (!RefreshCategoriesButton.IsEnabled)
        {
            return;
        }

        RefreshCategoriesButton.IsEnabled = false;

        try
        {
            await _categories.ApplyRulesCommand.ExecuteAsync(null);
        }
        finally
        {
            RefreshCategoriesButton.IsEnabled = true;
        }

        // 匹配结果已写库，若正按分类过滤则重放一次，保证内容与页头统计同步。
        if (_activeCategoryFilter is long categoryId)
        {
            var name = _categories.Categories.FirstOrDefault(c => c.Id == categoryId)?.Name;

            if (name is not null)
            {
                _ = _gallery.ApplyCategoryFilterAsync(categoryId, name);
            }
        }
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

        CategoriesNavItem.MenuItems.Clear();

        foreach (var category in _categories.Categories)
        {
            CategoriesNavItem.MenuItems.Add(new NavigationViewItem
            {
                Content = category.Name,
                Icon = new FontIcon { Glyph = FolderGlyph },
                Tag = $"{CategoryTagPrefix}{category.Id}"
            });
        }

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

        foreach (var element in (UIElement[])[SearchBox, SettingsButton])
        {
            if (element is not FrameworkElement { IsLoaded: true, ActualWidth: > 0 } framework)
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

    /// <summary>图库查询状态变化时刷新窗口层代理属性，驱动 loading 覆盖层装载与卸载。</summary>
    private void OnGalleryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.IsQuerying))
        {
            OnPropertyChanged(nameof(IsGalleryQuerying));
            SyncLoadingOverlay();
        }
    }

    /// <summary>让窗口层覆盖层与图库查询状态对齐，并在显示期间启用滑块动画。</summary>
    /// <remarks>
    /// ProgressBar 声明为 determinate，仅在显示时切 IsIndeterminate=true，隐藏即复位——
    /// 保证撤层后不留动画时钟。
    /// 2026-09-03 实测澄清：过去的 LayoutCycleException 与滑块动画无关，
    /// 成因是覆盖层曾被放在 GalleryPage 内与 GridView 同格（同一布局容器内交替失效），
    /// 该层已移除、覆盖层保留在窗口层，故此处可安全启用不确定态动画。
    /// 若日后把覆盖层移回页面内同格，必须同时撤掉本行的动画切换。
    /// </remarks>
    private void SyncLoadingOverlay()
    {
        var querying = _gallery.IsQuerying;

        LoadingOverlay.Visibility = querying
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 隐藏时复位为 determinate：覆盖层收起后不得残留动画时钟。
        LoadingProgressBar.IsIndeterminate = querying;
    }

    private void ApplySettings(AppSettings settings)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = MapTheme(settings.Theme);
        }

        // SetThumbnailSizeAsync 的同步段先更新尺寸值，随后的 ApplyThumbnailSize
        // 触发的属性通知才能读到新值，顺序不可颠倒。
        _ = _gallery.SetThumbnailSizeAsync(settings.ThumbnailSize);
        _galleryPage.ApplyViewMode(settings.ViewMode);
        _galleryPage.ApplyThumbnailSize();

        // 幻灯片间隔、播放顺序与切换方式：查看器不反向依赖设置服务，由外壳在设置变更时推送，
        // 放映途中改设置也能即时生效（ApplySettings 内部会保留播放状态续跑定时器）。
        _viewer.ApplySettings(settings);

        NotifyTargetChanged();
    }

    /// <summary>在图库中双击条目时打开查看器：图片走图片查看器，视频走播放器。</summary>
    /// <param name="item">被双击的条目。</param>
    /// <param name="startSlideShow">打开图片查看器后是否立即开始幻灯片播放。</param>
    public async Task OpenViewerAsync(MediaItemViewModel item, bool startSlideShow = false)
    {
        ArgumentNullException.ThrowIfNull(item);

        var items = _gallery.Items.Select(i => i.Item).ToList();
        var index = items.FindIndex(i => i.Id == item.Id);

        if (item.Item.Kind == MediaKind.Video)
        {
            // 播放器页延迟解析：其 MediaPlayerElement 在应用启动阶段构造会触发 WinRT 异常。
            var videoPage = App.Services.GetRequiredService<VideoPlayerPage>();

            IsViewerVisible = true;
            ShowPage(videoPage);
            OnChromeVisibilityChanged();

            await videoPage.OpenAsync(item.Item);
            return;
        }

        if (item.Item.Kind != MediaKind.Image)
        {
            return;
        }

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

        if (startSlideShow)
        {
            _viewer.StartSlideShowCommand.Execute(null);
        }
    }

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
    }

    private void OnChromeVisibilityChanged()
    {
        OnPropertyChanged(nameof(NavigationPaneMode));
    }

    /// <summary>关闭视频播放器，返回图库（图片查看器为独立窗口，经其自身 Close 关闭）。</summary>
    public void CloseViewer()
    {
        _viewer.StopSlideShow();

        // 离开播放器页会触发其 Unloaded，其中会释放 MediaPlayer；此处先确保退出全屏。
        ExitFullScreenIfNeeded();

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

    private void NotifyTargetChanged()
    {
        OnPropertyChanged(nameof(IsGalleryVisible));
        OnPropertyChanged(nameof(IsVideosVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
