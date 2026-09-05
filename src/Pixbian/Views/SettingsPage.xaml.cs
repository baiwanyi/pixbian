/**
 * 设置页代码后置（M2）。
 * 职责：把主题、幻灯片、扫描源与局域网访问的界面操作转交 ViewModel，并初始化各选择器的当前值。
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
using Microsoft.Windows.Storage.Pickers;
using Pixbian.Core.Models;
using Pixbian.ViewModels;

using Pixbian.Services;

namespace Pixbian.Views;

/// <summary>设置页。</summary>
public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private int _themeIndex;
    private int _transitionIndex;
    private bool _isWebSharingOn;

    /// <summary>内容块的最大宽度（逻辑像素）；超过时两侧对称留白居中。</summary>
    private const double ContentMaxWidth = 960;

    /// <summary>上次已应用的端口文本；用于判断展开区收起时是否真的需要重建服务。</summary>
    private string _appliedPortText = string.Empty;
    private readonly CategoryPage _categoryPage;

    /// <summary>初始化设置页。</summary>
    /// <param name="viewModel">设置视图模型，由依赖注入提供。</param>
    /// <param name="categoryPage">分类规则管理页，展开「分类管理」卡片时装载。</param>
    public SettingsPage(SettingsViewModel viewModel, CategoryPage categoryPage)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(categoryPage);

        ViewModel = viewModel;
        _categoryPage = categoryPage;

        _themeIndex = (int)viewModel.Theme;
        _transitionIndex = (int)viewModel.SlideShowTransition;

        InitializeComponent();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>设置视图模型。</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>承载本页的主窗口，用于为文件夹选择器提供归属 WindowId。</summary>
    public MainWindow Owner { get; set; } = null!;

    /// <summary>主题选择器的当前索引。</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetField(ref _themeIndex, value);
    }

    /// <summary>幻灯片切换模式选择器的当前索引。</summary>
    public int TransitionIndex
    {
        get => _transitionIndex;
        set => SetField(ref _transitionIndex, value);
    }

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

    /// <summary>分类管理展开时懒装载分类页；页面 Loaded 会自动加载分类与规则列表。</summary>
    private void OnCategoryExpanderExpanding(object sender, ExpanderExpandingEventArgs args) =>
        CategoryHost.Content ??= _categoryPage;

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
        SlideIntervalBox.Value = ViewModel.SlideShowIntervalSeconds;
        TransitionIndex = (int)ViewModel.SlideShowTransition;
        ThemeIndex = (int)ViewModel.Theme;
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

    /// <summary>展开区收起时应用端口与密码，替代原先的「保存并应用」按钮。</summary>
    /// <remarks>
    /// 端口与上次应用值相同且未输入新密码时直接跳过：ApplyWebSharingAsync 会销毁并重建
    /// 服务器实例，无谓重启会让已连接的局域网客户端断开。
    /// </remarks>
    private async void OnWebSettingsCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
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

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.Theme = (AppTheme)ThemeSelector.SelectedIndex;
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

    /// <summary>幻灯片间隔变更即落盘。</summary>
    /// <remarks>
    /// 关键约束：清空输入框时 Value 为 NaN（不是 0），此时必须直接返回，待用户填回有效数字再写入；
    /// 否则 0 会被钳制成 1 秒，而输入框仍显示为空，界面与设置就此不一致。
    /// </remarks>
    /// <param name="sender">触发事件的数字输入框。</param>
    /// <param name="args">新旧数值。</param>
    private void OnSlideIntervalValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        ViewModel.SlideShowIntervalSeconds = (int)Math.Clamp(args.NewValue, 1, 3600);
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
