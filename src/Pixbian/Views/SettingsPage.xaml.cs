/**
 * 设置页代码后置（主文件）。
 * 职责：持有视图模型与分类页实例、暴露共享绑定属性、页面加载时的初始同步与内容区限宽居中；
 *      分支职责见各 partial——偏好项（.Preferences.cs）、媒体库与音乐库（.Library.cs）、
 *      局域网访问（.WebSharing.cs）、收藏分组管理（.FavoriteGroups.cs）、
 *      数据与备份（.Backup.cs）。
 * 复用约定：文件夹统一通过 Microsoft.Windows.Storage.Pickers 的文件夹选择器选取，
 *          该 API 原生支持非打包应用，无需关联窗口句柄；视图模型与分类页由依赖注入在构造时传入。
 * 关键约束：下拉选择器的 SelectedIndex 与页面属性双向绑定，设置变更必须先落盘再通知外壳，
 *          顺序颠倒会导致重启后设置丢失；移除扫描源前需二次确认，该操作会清理索引记录。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>设置页。</summary>
public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    /// <summary>内容块的最大宽度（逻辑像素）；超过时两侧对称留白居中。</summary>
    private const double ContentMaxWidth = 960;

    private readonly CategoryPage _categoryPage;

    /// <summary>初始化设置页。</summary>
    /// <param name="viewModel">设置视图模型，由依赖注入提供。</param>
    /// <param name="categoryPage">分类规则管理页，作为「分类」组内容直接装载。</param>
    /// <param name="favoriteGroups">收藏分组视图模型，与主窗口侧栏、图库页共享同一实例。</param>
    /// <param name="backup">数据备份视图模型，供「数据与备份」分区的导出与导入使用。</param>
    public SettingsPage(
        SettingsViewModel viewModel,
        CategoryPage categoryPage,
        FavoriteGroupViewModel favoriteGroups,
        BackupViewModel backup)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(categoryPage);
        ArgumentNullException.ThrowIfNull(favoriteGroups);
        ArgumentNullException.ThrowIfNull(backup);

        ViewModel = viewModel;
        _categoryPage = categoryPage;
        FavoriteGroups = favoriteGroups;
        Backup = backup;

        _themeIndex = (int)viewModel.Theme;
        _wheelModeIndex = (int)viewModel.ViewerWheelMode;
        _initialZoomIndex = (int)viewModel.ViewerInitialZoom;
        _playOrderIndex = (int)viewModel.SlideShowOrder;
        _transitionIndex = (int)viewModel.SlideShowTransition;
        _backgroundMusicIndex = (int)viewModel.SlideShowBackgroundMusic;
        _bgmVolumePercent = (int)Math.Round(viewModel.SlideShowBackgroundMusicVolume * 100);
        _clipPresetIndex = Array.IndexOf(ClipPresetOptions, viewModel.SlideShowClipPresetSeconds);
        _slideIntervalIndex = Array.IndexOf(SlideIntervalOptions, viewModel.SlideShowIntervalSeconds);

        InitializeComponent();

        // 「分类」组无外层 Expander，分类页随设置页装载即就位；
        // 其数据加载由页面自身 Loaded 驱动。
        CategoryHost.Content = _categoryPage;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>设置视图模型。</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>收藏分组管理视图模型；其集合与主窗口侧栏、图库页共用，改动即时同步。</summary>
    public FavoriteGroupViewModel FavoriteGroups { get; }

    /// <summary>数据备份视图模型；承载「数据与备份」分区的导出与导入。</summary>
    public BackupViewModel Backup { get; }

    /// <summary>承载本页的主窗口，用于为文件夹选择器提供归属 WindowId。</summary>
    public MainWindow Owner { get; set; } = null!;

    /// <summary>把四个开关旁的状态文字同步为开关当前值；开关回填与用户切换后都要调用。</summary>
    private void SyncToggleStateLabels()
    {
        FullVideoStateLabel.Text = IncludeVideosToggle.IsOn ? "开启" : "关闭";
        SilentPlaybackStateLabel.Text = VideoMutedToggle.IsOn ? "开启" : "关闭";
        BlurBackdropStateLabel.Text = BlurBackdropToggle.IsOn ? "开启" : "关闭";
        AnimationStateLabel.Text = AnimationToggle.IsOn ? "开启" : "关闭";
    }

    /// <summary>在视觉树中按名称深度优先查找指定类型的后代元素（模板内元素对 FindName 不可见）。</summary>
    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T typed && typed is FrameworkElement { Name: var elementName } && elementName == name)
            {
                return typed;
            }

            var descendant = FindDescendantByName<T>(child, name);

            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    /// <summary>内容块限宽居中：视口可用宽超过上限时两侧对称留白，否则撑满。</summary>
    /// <remarks>
    /// 用代码计算对称 Margin 而非 HorizontalAlignment：Center 会让 StackPanel 收缩到
    /// 内容自然宽（卡片全部自适应，自然宽远小于上限），MaxWidth 形同虚设；
    /// Stretch + MaxWidth 在超出上限后的摆放位置依赖框架对齐细则。
    /// 对称 Margin 直接表达「居中」语义，不依赖框架版本的布局行为。
    /// </remarks>
    /// <param name="sender">触发事件的滚动容器。</param>
    /// <param name="e">新尺寸。</param>
    private void OnContentViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 48 = ScrollViewer 左右内边距（24 × 2），扣除后才是内容可用宽。
        const double horizontalPadding = 48;

        var slack = Math.Max(0, (e.NewSize.Width - horizontalPadding - ContentMaxWidth) / 2);
        ContentRoot.Margin = new Thickness(slack, 0, slack, 0);
    }

    /// <summary>页面加载时载入扫描源列表，并同步 Web 与幻灯片区块的控件状态。</summary>
    public async Task InitializeAsync()
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
        await ViewModel.LoadMusicFoldersCommand.ExecuteAsync(null);
        await FavoriteGroups.LoadAsync();
        SyncWebSharingControls();
        SyncSlideShowControls();
        SyncBackupControls();
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
