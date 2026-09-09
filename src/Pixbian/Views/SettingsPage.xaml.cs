/**
 * 设置页代码后置（M2）。
 * 职责：把主题、幻灯片、媒体库（图库扫描源与音乐库目录）与局域网访问的界面操作转交 ViewModel，并初始化各选择器的当前值。
 * 复用约定：文件夹统一通过 Microsoft.Windows.Storage.Pickers 的文件夹选择器选取，
 *          该 API 原生支持非打包应用，无需关联窗口句柄；视图模型与分类页由依赖注入在构造时传入。
 * 关键约束：下拉选择器的 SelectedIndex 与页面属性双向绑定，设置变更必须先落盘再通知外壳，
 *          顺序颠倒会导致重启后设置丢失；
 *          NumberBox 清空输入时 Value 为 NaN 而非 0，写回设置前必须拦截，
 *          否则会被钳制成 1 秒，导致界面显示与用户输入不一致；
 *          移除扫描源前需二次确认，该操作会清理索引记录。
 */

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.ViewModels;

using Pixbian.Services;

namespace Pixbian.Views;

/// <summary>设置页。</summary>
public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private int _themeIndex;
    private int _wheelModeIndex;
    private int _initialZoomIndex;
    private int _playOrderIndex;
    private int _transitionIndex;
    private int _backgroundMusicIndex;
    private int _bgmVolumePercent;
    private int _clipPresetIndex;
    private int _slideIntervalIndex;
    private bool _isWebSharingOn;

    /// <summary>内容块的最大宽度（逻辑像素）；超过时两侧对称留白居中。</summary>
    private const double ContentMaxWidth = 960;

    /// <summary>上次已应用的端口文本；用于判断输入框失焦时是否真的需要重建服务。</summary>
    private string _appliedPortText = string.Empty;
    private readonly CategoryPage _categoryPage;
    private FavoriteGroup? _selectedGroup;

    /// <summary>幻灯片间隔下拉的可选秒数（与选项顺序一致）。</summary>
    private static readonly int[] SlideIntervalOptions = { 1, 3, 5, 10, 20, 30, 60 };

    /// <summary>片段时长上限下拉的可选秒数（与共享裁决器保持一致）。</summary>
    private static readonly int[] ClipPresetOptions = ClipRangePlanner.PresetOptions;

    /// <summary>初始化设置页。</summary>
    /// <param name="viewModel">设置视图模型，由依赖注入提供。</param>
    /// <param name="categoryPage">分类规则管理页，作为「分类」组内容直接装载。</param>
    /// <param name="favoriteGroups">收藏分组视图模型，与主窗口侧栏、图库页共享同一实例。</param>
    public SettingsPage(
        SettingsViewModel viewModel,
        CategoryPage categoryPage,
        FavoriteGroupViewModel favoriteGroups)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(categoryPage);
        ArgumentNullException.ThrowIfNull(favoriteGroups);

        ViewModel = viewModel;
        _categoryPage = categoryPage;
        FavoriteGroups = favoriteGroups;

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

    /// <summary>列表中被选中的分组；非空时下方输入区切换为改名模式。</summary>
    public FavoriteGroup? SelectedGroup
    {
        get => _selectedGroup;
        set => SetField(ref _selectedGroup, value);
    }

    /// <summary>是否处于分组改名状态；驱动新增区与改名区的互斥显隐。</summary>
    public bool IsEditingGroup => SelectedGroup is not null;

    /// <summary>承载本页的主窗口，用于为文件夹选择器提供归属 WindowId。</summary>
    public MainWindow Owner { get; set; } = null!;

    /// <summary>主题选择器的当前索引。</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetField(ref _themeIndex, value);
    }

    /// <summary>鼠标滚轮行为选择器的当前索引。</summary>
    public int WheelModeIndex
    {
        get => _wheelModeIndex;
        set => SetField(ref _wheelModeIndex, value);
    }

    /// <summary>缩放首选项选择器的当前索引。</summary>
    public int InitialZoomIndex
    {
        get => _initialZoomIndex;
        set => SetField(ref _initialZoomIndex, value);
    }

    /// <summary>幻灯片播放顺序选择器的当前索引。</summary>
    public int PlayOrderIndex
    {
        get => _playOrderIndex;
        set => SetField(ref _playOrderIndex, value);
    }

    /// <summary>幻灯片切换模式选择器的当前索引。</summary>
    public int TransitionIndex
    {
        get => _transitionIndex;
        set => SetField(ref _transitionIndex, value);
    }

    /// <summary>片段时长上限选择器的当前索引。</summary>
    public int ClipPresetIndex
    {
        get => _clipPresetIndex;
        set => SetField(ref _clipPresetIndex, value);
    }

    /// <summary>幻灯片间隔选择器的当前索引。</summary>
    public int SlideIntervalIndex
    {
        get => _slideIntervalIndex;
        set => SetField(ref _slideIntervalIndex, value);
    }

    /// <summary>背景音乐模式选择器的当前索引。</summary>
    public int BackgroundMusicIndex
    {
        get => _backgroundMusicIndex;
        set => SetField(ref _backgroundMusicIndex, value);
    }

    /// <summary>背景音乐音量的百分比显示文本。</summary>
    public string BgmVolumeText => $"{_bgmVolumePercent}%";

    /// <summary>局域网开关是否打开；驱动「端口与密码」展开区的显隐与右侧状态文字。</summary>
    public bool IsWebSharingOn
    {
        get => _isWebSharingOn;
        set
        {
            if (_isWebSharingOn == value)
            {
                return;
            }

            _isWebSharingOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WebSharingStateText));
        }
    }

    /// <summary>开关状态文字：置于开关左侧。</summary>
    public string WebSharingStateText => IsWebSharingOn ? "开启" : "关闭";

    /// <summary>把四个开关旁的状态文字同步为开关当前值；开关回填与用户切换后都要调用。</summary>
    private void SyncToggleStateLabels()
    {
        FullVideoStateLabel.Text = IncludeVideosToggle.IsOn ? "开启" : "关闭";
        SilentPlaybackStateLabel.Text = VideoMutedToggle.IsOn ? "开启" : "关闭";
        BlurBackdropStateLabel.Text = BlurBackdropToggle.IsOn ? "开启" : "关闭";
        AnimationStateLabel.Text = AnimationToggle.IsOn ? "开启" : "关闭";
    }

    /// <summary>媒体库「位置」行 Expander 载入后归零 Header 的内边距。</summary>
    /// <remarks>
    /// Expander 模板 Header 是 ToggleButton，其 Padding 由样式 Setter 以 StaticResource 提供
    /// （generic.xaml 加载时一次性解析 16,0,0,0，实例级资源覆盖无法穿透 StaticResource），
    /// 只能在模板实例化后经视觉树定位，以本地值归零——使「位置」行的左缘与「刷新库」普通行对齐。
    /// </remarks>
    private void OnLibraryExpanderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        if (FindDescendantByName<ToggleButton>(expander, "ExpanderHeader") is { } header)
        {
            header.Padding = new Thickness(0);
        }
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
    }

    /// <summary>按当前设置同步 Web 区块的控件状态。</summary>
    private void SyncWebSharingControls()
    {
        var settings = ViewModel.Settings;

        WebSharingToggle.IsOn = settings.IsWebSharingEnabled;
        IsWebSharingOn = settings.IsWebSharingEnabled;
        WebPortBox.Text = settings.WebSharingPort.ToString(CultureInfo.InvariantCulture);
        _appliedPortText = WebPortBox.Text;
    }

    /// <summary>按当前设置同步幻灯片区块的控件状态。</summary>
    /// <remarks>
    /// 间隔不做双向绑定：NumberBox.Value 是 double 而设置项是 int，绑定会在用户键入中间态
    /// （如刚敲下「3」准备输「30」）就写盘，产生多余 IO。改由 ValueChanged 事件落盘，
    /// 此处只负责进入页面时的回填；两个下拉的索引是 int 对 int，可安全双向绑定，
    /// 这里重复赋值只为覆盖其他入口改过设置的情形，SetField 会自行判等。
    /// </remarks>
    private void SyncSlideShowControls()
    {
        SlideIntervalSelector.SelectedIndex =
            Math.Max(0, Array.IndexOf(SlideIntervalOptions, ViewModel.SlideShowIntervalSeconds));
        PlayOrderIndex = (int)ViewModel.SlideShowOrder;
        TransitionIndex = (int)ViewModel.SlideShowTransition;
        ThemeIndex = (int)ViewModel.Theme;
        WheelModeIndex = (int)ViewModel.ViewerWheelMode;
        InitialZoomIndex = (int)ViewModel.ViewerInitialZoom;
        BackgroundMusicIndex = (int)ViewModel.SlideShowBackgroundMusic;
        BgmVolumeSlider.Value = ViewModel.SlideShowBackgroundMusicVolume * 100;
        ClipPresetSelector.SelectedIndex =
            Array.IndexOf(ClipPresetOptions, ViewModel.SlideShowClipPresetSeconds);

        // 开关不走绑定：视觉切换完全由用户交互驱动（回写绑定会造成点击迟钝），此处只做回填。
        IncludeVideosToggle.IsOn = ViewModel.SlideShowFullVideoPlayback;
        VideoMutedToggle.IsOn = ViewModel.SlideShowSilentPlayback;
        BlurBackdropToggle.IsOn = ViewModel.SlideShowBlurBackdrop;
        AnimationToggle.IsOn = ViewModel.SlideShowAnimationEnabled;

        // 回填会触发 Toggled 事件，状态文字必须在此统一刷新（事件路径上 x:Bind 通知不可靠）。
        SyncToggleStateLabels();
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        // Windows App SDK 的 Microsoft.Windows.Storage.Pickers 原生支持非打包应用，
        // 构造时传入 WindowId 即完成归属，不再需要 InitializeWithWindow 关联句柄。
        var picker = new FolderPicker(Owner.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        var folder = await picker.PickSingleFolderAsync();

        if (folder is not null)
        {
            await ViewModel.AddFolderCommand.ExecuteAsync(folder.Path);
        }
    }

    /// <summary>「音乐库」分区的添加按钮：选取目录后加入音乐库并立即重扫。</summary>
    private async void OnAddMusicFolderClick(object sender, RoutedEventArgs e)
    {
        // 与扫描源同一选择器方案；起始位置取音乐库以贴合本操作的语义。
        var picker = new FolderPicker(Owner.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary
        };

        var folder = await picker.PickSingleFolderAsync();

        if (folder is not null)
        {
            await ViewModel.AddMusicFolderCommand.ExecuteAsync(folder.Path);
        }
    }

    /// <summary>移除音乐目录：二次确认后从设置中剔除并重扫，曲目随之失效。</summary>
    private async void OnRemoveMusicFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicFolderRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "移除音乐目录",
            Content = $"短片页将不再从这里选取背景音乐（不会删除磁盘文件）。\n\n{row.Path}",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RemoveMusicFolderCommand.ExecuteAsync(row);
    }

    private async void OnStartIndexingClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartIndexingCommand.ExecuteAsync(null);
    }

    /// <summary>开关切换即生效，并驱动「端口与密码」展开区的显隐。</summary>
    private async void OnWebSharingToggled(object sender, RoutedEventArgs e)
    {
        IsWebSharingOn = WebSharingToggle.IsOn;
        await ApplyWebSharingAsync();
    }

    /// <summary>完整视频开关：切换即落盘，放映下次装载视频时按新策略计算区间。</summary>
    private void OnFullVideoToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowFullVideoPlayback = IncludeVideosToggle.IsOn;
    }

    /// <summary>静音播放开关：切换即落盘，放映经 ApplySettings 推送即时重算音频策略。</summary>
    private void OnVideoMutedToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowSilentPlayback = VideoMutedToggle.IsOn;
    }

    /// <summary>背景虚化开关：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnBlurBackdropToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowBlurBackdrop = BlurBackdropToggle.IsOn;
    }

    /// <summary>背景音乐模式：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnBackgroundMusicSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SlideShowBackgroundMusic = (BackgroundMusicMode)BackgroundMusicSelector.SelectedIndex;

    /// <summary>画面动画开关：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnAnimationToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowAnimationEnabled = AnimationToggle.IsOn;
    }

    /// <summary>片段时长上限：切换即落盘，放映下次装载视频时按新档位计算区间。</summary>
    private void OnClipPresetSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SlideShowClipPresetSeconds = ClipPresetOptions[ClipPresetSelector.SelectedIndex];

    /// <summary>幻灯片间隔：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnSlideIntervalSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SlideShowIntervalSeconds = SlideIntervalOptions[SlideIntervalSelector.SelectedIndex];

    /// <summary>背景音乐音量：百分比换算为 0–1 落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnBgmVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var percent = (int)Math.Round(e.NewValue);

        if (_bgmVolumePercent == percent)
        {
            return;
        }

        _bgmVolumePercent = percent;
        ViewModel.SlideShowBackgroundMusicVolume = percent / 100.0;
        OnPropertyChanged(nameof(BgmVolumeText));
    }

    /// <summary>端口或密码输入框失焦时应用配置，替代「保存」按钮。</summary>
    /// <remarks>
    /// 端口与上次应用值相同且未输入新密码时直接跳过：ApplyWebSharingAsync 会销毁并重建
    /// 服务器实例，无谓重启会让已连接的局域网客户端断开。
    /// </remarks>
    private async void OnWebSettingLostFocus(object sender, RoutedEventArgs e)
    {
        var portChanged = !string.Equals(WebPortBox.Text, _appliedPortText, StringComparison.Ordinal);

        if (!portChanged && WebPasswordBox.Password.Length == 0)
        {
            return;
        }

        await ApplyWebSharingAsync();
    }

    /// <summary>按控件当前值应用局域网配置。</summary>
    private async Task ApplyWebSharingAsync()
    {
        var port = int.TryParse(WebPortBox.Text, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 8756;

        await ViewModel.ApplyWebSharingAsync(
            WebSharingToggle.IsOn,
            port,
            WebPasswordBox.Password);

        // 密码只存哈希、不回显明文，应用后立即清空输入框。
        WebPasswordBox.Password = string.Empty;
        _appliedPortText = WebPortBox.Text;
        OnPropertyChanged(nameof(ViewModel.WebStatusText));
    }

    /// <summary>踢出全部活跃会话：所有已登录设备需重新登录。</summary>
    private void OnRevokeSessionsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RevokeAllSessions();
    }

    /// <summary>踢出单个活跃会话：会话的公开 ID 经按钮 Tag 传入（非认证令牌）。</summary>
    private void OnRevokeSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sessionId })
        {
            ViewModel.RevokeSessionById(sessionId);
        }
    }

    /// <summary>点击访问地址：交给系统默认浏览器打开。</summary>
    /// <remarks>
    /// 显式走 ShellExecute（UseShellExecute 单参数、无命令拼接），不依赖
    /// HyperlinkButton.NavigateUri 的桌面自动行为；URL 先经绝对 URI 校验，
    /// 且来源为本机 Web 服务自身的监听地址，非任意外部输入。
    /// </remarks>
    private void OnWebAccessUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    private async void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryFolderRow row })
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
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RemoveFolderCommand.ExecuteAsync(row);
    }

    /// <summary>选中或取消选中分组：进入 / 退出改名模式，并刷新两个输入区的互斥显隐。</summary>
    private void OnFavoriteGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 直接读控件的 SelectedItem 而非绑定回写的页面属性：x:Bind TwoWay 的回写时序
        // 不保证早于本事件，读页面属性可能拿到上一次的选中项。
        SelectedGroup = FavoriteGroupList.SelectedItem as FavoriteGroup;

        if (SelectedGroup is { } group)
        {
            FavoriteGroups.BeginEditGroup(group);
        }
        else
        {
            FavoriteGroups.CancelEditGroup();
        }

        OnPropertyChanged(nameof(IsEditingGroup));
    }

    /// <summary>新增分组：名称先经代码后置回写 VM，避免 TextBox 在失焦才更新源、校验取到旧值。</summary>
    private async void OnAddFavoriteGroupClick(object sender, RoutedEventArgs e)
    {
        FavoriteGroups.NewGroupName = NewFavoriteGroupNameBox.Text;
        await FavoriteGroups.AddGroupAsync();

        NewFavoriteGroupNameBox.Text = string.Empty;
    }

    /// <summary>保存分组改名。</summary>
    private async void OnRenameFavoriteGroupClick(object sender, RoutedEventArgs e)
    {
        await FavoriteGroups.RenameGroupAsync();
        ClearGroupSelection();
    }

    /// <summary>取消改名：清空列表选择。</summary>
    private void OnCancelEditFavoriteGroupClick(object sender, RoutedEventArgs e) => ClearGroupSelection();

    /// <summary>删除分组：二次确认后解除其下全部归属，条目收藏状态与磁盘文件均不受影响。</summary>
    private async void OnDeleteFavoriteGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteGroup group })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除分组",
            Content = $"分组「{group.Name}」下的条目将回到未分组（收藏状态与文件均不受影响）。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ClearGroupSelection();
        await FavoriteGroups.DeleteGroupAsync(group);
    }

    /// <summary>清空列表选择并退出改名模式。集合整体重建后必须调用，否则改名区会停留在已删除的分组上。</summary>
    private void ClearGroupSelection()
    {
        FavoriteGroupList.SelectedItem = null;
        SelectedGroup = null;
        FavoriteGroups.CancelEditGroup();
        OnPropertyChanged(nameof(IsEditingGroup));
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.Theme = (AppTheme)ThemeSelector.SelectedIndex;
    }

    /// <summary>鼠标滚轮行为变更即落盘。</summary>
    private void OnWheelModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WheelModeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ViewerWheelMode = (ViewerWheelMode)WheelModeSelector.SelectedIndex;
    }

    /// <summary>缩放首选项变更即落盘。</summary>
    private void OnInitialZoomSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InitialZoomSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ViewerInitialZoom = (ViewerInitialZoom)InitialZoomSelector.SelectedIndex;
    }

    /// <summary>幻灯片播放顺序变更即落盘。</summary>
    private void OnPlayOrderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlayOrderSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.SlideShowOrder = (SlideShowPlayOrder)PlayOrderSelector.SelectedIndex;
    }

    /// <summary>幻灯片切换模式变更即落盘。</summary>
    private void OnTransitionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransitionSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.SlideShowTransition = (SlideShowTransitionMode)TransitionSelector.SelectedIndex;
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
