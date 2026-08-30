/**
 * 设置页代码后置（M2）。
 * 职责：把外观设置与扫描源管理的界面操作转交 ViewModel，并初始化各选择器的可选项与当前值。
 * 复用约定：文件夹统一通过系统文件夹选择器选取，选取前必须用窗口句柄初始化选择器，
 *          否则在非打包应用中会抛出 COM 异常；视图模型由依赖注入在构造时传入。
 * 关键约束：SelectedIndex 与 ViewModel 之间是双向同步，设置变更必须先落盘再通知外壳，
 *          顺序颠倒会导致重启后设置丢失；移除扫描源前需二次确认，该操作会清理索引记录。
 */

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Models;
using Pixbian.ViewModels;
using WinRT.Interop;

namespace Pixbian.Views;

/// <summary>设置页。</summary>
public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private int _themeIndex;
    private int _viewModeIndex;
    private int _thumbnailSizeIndex;

    /// <summary>初始化设置页。</summary>
    /// <param name="viewModel">设置视图模型，由依赖注入提供。</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        _themeIndex = (int)viewModel.Theme;
        _viewModeIndex = MapViewModeToIndex(viewModel.ViewMode);
        _thumbnailSizeIndex = IndexOfPreset(viewModel.ThumbnailSize);

        InitializeComponent();
        PopulateThumbnailSizes();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>设置视图模型。</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>主题选择器的当前索引。</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetField(ref _themeIndex, value);
    }

    /// <summary>视图模式选择器的当前索引。</summary>
    public int ViewModeIndex
    {
        get => _viewModeIndex;
        set => SetField(ref _viewModeIndex, value);
    }

    /// <summary>缩略图尺寸选择器的当前索引。</summary>
    public int ThumbnailSizeIndex
    {
        get => _thumbnailSizeIndex;
        set => SetField(ref _thumbnailSizeIndex, value);
    }

    /// <summary>页面加载时载入扫描源列表并初始化 Web 区块。</summary>
    public async Task InitializeAsync()
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
        SyncWebSharingControls();
    }

    /// <summary>按当前设置同步 Web 区块的控件状态。</summary>
    private void SyncWebSharingControls()
    {
        var settings = ViewModel.Settings;

        WebSharingToggle.IsOn = settings.IsWebSharingEnabled;
        WebPortBox.Text = settings.WebSharingPort.ToString(CultureInfo.InvariantCulture);
    }

    private void PopulateThumbnailSizes()
    {
        ThumbnailSizeSelector.Items.Clear();

        foreach (var size in ThumbnailSizes.Presets)
        {
            ThumbnailSizeSelector.Items.Add($"{size} px");
        }

        ThumbnailSizeSelector.SelectedIndex = _thumbnailSizeIndex;
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add("*");

        // 非打包应用必须显式关联窗口句柄，否则选择器无法弹出。
        var windowHandle = WindowNative.GetWindowHandle(GetOwningWindow());
        InitializeWithWindow.Initialize(picker, windowHandle);

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

    private void OnWebSharingToggled(object sender, RoutedEventArgs e)
    {
        // 仅切换开关不立即生效，由「保存并应用」统一处理，避免半套配置被应用。
    }

    private async void OnApplyWebSharingClick(object sender, RoutedEventArgs e)
    {
        var port = int.TryParse(WebPortBox.Text, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 8756;

        await ViewModel.ApplyWebSharingAsync(
            WebSharingToggle.IsOn,
            port,
            WebPasswordBox.Password);

        WebPasswordBox.Password = string.Empty;
        OnPropertyChanged(nameof(ViewModel.WebStatusText));
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

    private void OnViewModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ViewMode = MapIndexToViewMode(ViewModeSelector.SelectedIndex);
    }

    /// <summary>视图模式映射为选择器索引（网格 0 / 自适应 1），与枚举数值解耦。</summary>
    private static int MapViewModeToIndex(GalleryViewMode mode) =>
        mode == GalleryViewMode.Grid ? 0 : 1;

    /// <summary>选择器索引映射为视图模式，与枚举数值解耦。</summary>
    private static GalleryViewMode MapIndexToViewMode(int index) =>
        index == 0 ? GalleryViewMode.Grid : GalleryViewMode.Justified;

    private void OnThumbnailSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailSizeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ThumbnailSize = ThumbnailSizes.Presets[ThumbnailSizeSelector.SelectedIndex];
    }

    /// <summary>定位所属窗口，用于初始化文件夹选择器所需的窗口句柄。</summary>
    private static MainWindow GetOwningWindow() =>
        App.Services.GetRequiredService<MainWindow>();

    /// <summary>查找尺寸档位在预设列表中的索引，未命中时回落到默认档位。</summary>
    private static int IndexOfPreset(int size)
    {
        var presets = ThumbnailSizes.Presets;

        for (var i = 0; i < presets.Count; i++)
        {
            if (presets[i] == size)
            {
                return i;
            }
        }

        for (var i = 0; i < presets.Count; i++)
        {
            if (presets[i] == ThumbnailSizes.Default)
            {
                return i;
            }
        }

        return 0;
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
