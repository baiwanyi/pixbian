/**
 * 主窗口代码后置（主文件）：自定义标题栏 + 三栏布局外壳。
 * 职责：持有页面实例与视图模型、建立各子系统的事件订阅与窗口级装配、启动时初始化外壳与图库、
 *      暴露绑定属性；分支职责见各 partial——导航交互与跳转（.Navigation.cs）、导航项列表与
 *      扫描源操作（.NavigationItems.cs）、查看器与播放态（.Viewer.cs）、窗口外壳与主题（.Window.cs）。
 * 复用约定：页面实例与视图模型均由依赖注入提供；主题映射统一在 App.MapTheme 中完成，
 *          领域层的 AppTheme 与 WinUI 的 ElementTheme 只在此处转换。
 * 关键约束：主题必须设置在窗口内容根元素上，设在 Window 本身对 WinUI 3 无效；
 *          设置页需异步加载扫描源，故导航到设置页时必须触发一次初始化，不能只在启动时加载。
 */

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;
using Pixbian.Services;
using Pixbian.ViewModels;
using Pixbian.WebServer;

namespace Pixbian.Views;

/// <summary>Pixbian 主窗口。</summary>
public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ShellViewModel _shell;
    private readonly GalleryViewModel _gallery;
    private readonly SettingsViewModel _settings;
    private readonly ImageViewerViewModel _viewer;
    private readonly CategoryViewModel _categories;
    private readonly FavoriteGroupViewModel _favoriteGroups;
    private readonly IThumbnailService _thumbnails;
    private readonly GalleryPage _galleryPage;
    private readonly SettingsPage _settingsPage;
    private readonly ShortPage _shortPage;

    private readonly BackupViewModel _backup;

    /// <summary>初始化主窗口。</summary>
    /// <param name="shell">外壳视图模型。</param>
    /// <param name="gallery">图库视图模型。</param>
    /// <param name="settings">设置视图模型。</param>
    /// <param name="viewer">图片查看器视图模型。</param>
    /// <param name="categories">分类视图模型，驱动左栏分类子项。</param>
    /// <param name="favoriteGroups">收藏分组视图模型，驱动左栏收藏夹子项。</param>
    /// <param name="backup">数据备份视图模型，负责启动时的备份同步检查。</param>
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
        FavoriteGroupViewModel favoriteGroups,
        BackupViewModel backup,
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
        ArgumentNullException.ThrowIfNull(favoriteGroups);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(galleryPage);
        ArgumentNullException.ThrowIfNull(settingsPage);
        ArgumentNullException.ThrowIfNull(shortPage);

        _shell = shell;
        _gallery = gallery;
        _settings = settings;
        _viewer = viewer;
        _categories = categories;
        _favoriteGroups = favoriteGroups;
        _backup = backup;
        _thumbnails = thumbnails;
        _galleryPage = galleryPage;
        _settingsPage = settingsPage;
        _shortPage = shortPage;

        // 查看器移交放映：页面单例对窗口单例，构造期订阅一次即可（两者与主窗口同生命周期）。
        _viewer.SlideShowHandoffRequested += OnViewerSlideShowHandoffRequested;

        InitializeComponent();

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

        // 标题栏品牌 logo 按实际生效主题换源（浅色模式用深色图、深色模式用浅色图），
        // 加载后 ActualTheme 才已按系统主题解析，故首刷放在 RootGrid.Loaded。

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

        // 左栏动态子项由三个视图模型的集合驱动：设置页增删扫描源、分类页增删分类、
        // 设置页增删收藏分组，三处改动都自动同步到左栏。
        _settings.Folders.CollectionChanged += OnFoldersChanged;
        _categories.Categories.CollectionChanged += OnCategoriesChanged;
        _favoriteGroups.Groups.CollectionChanged += OnFavoriteGroupsChanged;

        // 分类集合仅由分类管理页的 Loaded 填充，主窗口须在启动时主动加载一次，
        // 左栏「分类」子项才能与「图库」子项一样随应用启动展开显示。
        _ = _categories.LoadAsync();

        // 收藏分组同样在设置页 InitializeAsync 里加载，主窗口须先加载一次，
        // 左栏收藏夹子项才能在启动进入收藏夹时就位。
        _ = _favoriteGroups.LoadAsync();

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
            AppLog.Error("Shell", "初始化外壳设置与图库失败。", ex);
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
                AppLog.Error("Shell", "自动恢复局域网共享失败。", ex);
            }
        }
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
