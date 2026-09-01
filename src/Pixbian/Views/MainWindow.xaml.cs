/**
 * 主窗口代码后置（M2）：自定义标题栏 + 三栏布局外壳，负责导航切换、搜索下发、主题应用、详情面板展示与窗口图标设置。
 * 职责：把导航项映射为页面可见性，把搜索输入转交给外壳视图模型，并响应设置变化重新应用主题；
 *      标题栏延伸进客户区后，交互控件须注册 Passthrough 区域才能接收指针输入；
 *      左栏「分类」「图库」为分组标题，子项由扫描源与分类集合驱动动态重建，
 *      选中子项时切到图库页按文件夹或分类过滤，分组标题内联按钮提供添加文件夹与批量重新匹配。
 * 复用约定：页面实例与视图模型均由依赖注入提供；主题映射统一在 MapTheme 中完成，
 *          领域层的 AppTheme 与 WinUI 的 ElementTheme 只在此处转换。
 * 关键约束：主题必须设置在窗口内容根元素上，设在 Window 本身对 WinUI 3 无效；
 *          Passthrough 矩形为物理像素，须按 RawPixelsPerViewPixel 换算，且在布局与激活变化时刷新；
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;
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
    private readonly ImageViewerPage _viewerPage;

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
    /// <param name="viewerPage">图片查看器页实例。</param>
    public MainWindow(
        ShellViewModel shell,
        GalleryViewModel gallery,
        SettingsViewModel settings,
        ImageViewerViewModel viewer,
        CategoryViewModel categories,
        IThumbnailService thumbnails,
        GalleryPage galleryPage,
        SettingsPage settingsPage,
        ImageViewerPage viewerPage)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(gallery);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(galleryPage);
        ArgumentNullException.ThrowIfNull(settingsPage);
        ArgumentNullException.ThrowIfNull(viewerPage);

        _shell = shell;
        _gallery = gallery;
        _settings = settings;
        _viewer = viewer;
        _categories = categories;
        _thumbnails = thumbnails;
        _galleryPage = galleryPage;
        _settingsPage = settingsPage;
        _viewerPage = viewerPage;

        InitializeComponent();

        // Mica 背景：unpackaged 应用默认没有 Windows 11 的窗口圆角，
        // 启用系统背景材质后轮廓才由 DWM 合成。
        // 材质本身被不透明的全窗口背景图完全覆盖，不影响观感；保留它只为获得窗口圆角
        // ——当前 SDK 未提供 TransparentBackdrop，没有代价更低的替代。
        SystemBackdrop = new MicaBackdrop();

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

        // 左栏动态子项由两个视图模型的集合驱动：设置页增删扫描源、分类页增删分类后自动同步。
        _settings.Folders.CollectionChanged += OnFoldersChanged;
        _categories.Categories.CollectionChanged += OnCategoriesChanged;

        // 「收藏夹」排在菜单首位，启动选中项须按 Tag 定位到图库分组。
        NavigationViewControl.SelectedItem =
            FindNavItem(NavigationViewControl.MenuItems, "AllPhotos") ?? NavigationViewControl.MenuItems[0];
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
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        // 左栏动态子项：按文件夹或分类过滤图库，导航上下文保持图库语义。
        if (tag.StartsWith(MediaFolderTagPrefix, StringComparison.Ordinal))
        {
            if (long.TryParse(tag.AsSpan(MediaFolderTagPrefix.Length), out var folderId))
            {
                SelectMediaFolder(folderId);
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

    /// <summary>选中图库文件夹子项：切到图库页并按该文件夹过滤。</summary>
    private void SelectMediaFolder(long folderId)
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
        PageHost.Content = _galleryPage;
        OnPropertyChanged(nameof(SearchPlaceholder));
        _ = _gallery.ApplyMediaFolderFilterAsync(folder.Path, folder.DisplayName);
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
        PageHost.Content = _galleryPage;
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
        OnChromeVisibilityChanged();
    }

    /// <summary>把当前导航目标对应的页面实例装载到内容宿主。</summary>
    private void ApplyCurrentPage(NavigationTarget target)
    {
        switch (target)
        {
            case NavigationTarget.Settings:
                PageHost.Content = _settingsPage;
                break;

            case NavigationTarget.Videos:
            case NavigationTarget.Favorites:
            case NavigationTarget.AllPhotos:
                PageHost.Content = _galleryPage;
                break;
        }
    }

    /// <summary>标题栏「设置」按钮：跳转设置页。</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e) => NavigateToSettings();

    /// <summary>跳转设置页：已在该页时忽略，否则选中对应导航项以复用标准导航流程。</summary>
    private void NavigateToSettings()
    {
        if (NavigationViewControl.SelectedItem is NavigationViewItem { Tag: "Settings" })
        {
            return;
        }

        var item = FindNavItem(NavigationViewControl.MenuItems, "Settings");

        if (item is not null)
        {
            NavigationViewControl.SelectedItem = item;
        }
    }

    /// <summary>左栏「添加文件夹」按钮：选取文件夹后仅加入媒体库扫描源，不触发索引。</summary>
    private async void OnAddMediaFolderClick(object sender, RoutedEventArgs e)
    {
        // 与设置页同一选择器方案：Windows App SDK 的 Picker 原生支持非打包应用，
        // 构造传入 WindowId 即完成归属。
        var picker = new FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        var folder = await picker.PickSingleFolderAsync();

        if (folder is not null)
        {
            await _settings.AddFolderCommand.ExecuteAsync(folder.Path);
        }
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
            GalleryNavItem.MenuItems.Add(new NavigationViewItem
            {
                Content = folder.DisplayName,
                Icon = new FontIcon { Glyph = "\uE8B7" },
                Tag = $"{MediaFolderTagPrefix}{folder.Folder.Id}"
            });
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
                Icon = new FontIcon { Glyph = "\uE8B7" },
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
            PageHost.Content = videoPage;
            OnChromeVisibilityChanged();

            await videoPage.OpenAsync(item.Item);
            return;
        }

        if (item.Item.Kind != MediaKind.Image)
        {
            return;
        }

        await _viewer.LoadPlaylistAsync(items, Math.Max(0, index));

        if (startSlideShow)
        {
            _viewer.StartSlideShowCommand.Execute(null);
        }

        IsViewerVisible = true;
        PageHost.Content = _viewerPage;
        OnChromeVisibilityChanged();
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

    /// <summary>关闭查看器或播放器，返回图库。</summary>
    public void CloseViewer()
    {
        _viewer.StopSlideShow();

        // 离开播放器页会触发其 Unloaded，其中会释放 MediaPlayer；此处先确保退出全屏。
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            ToggleFullScreen();
        }

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
