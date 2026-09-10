/**
 * 设置页代码后置——媒体库与音乐库（partial）。
 * 职责：图库扫描源与音乐目录的添加 / 移除、手动触发索引、媒体库「位置」行 Expander
 *      的头部内边距归零。
 * 复用约定：目录选取统一经 Microsoft.Windows.Storage.Pickers 的 FolderPicker（构造传
 *          Owner.AppWindow.Id 完成归属）；增删一律委托 SettingsViewModel 的 Command。
 * 关键约束：移除扫描源前需二次确认（会清理索引记录）；Expander 头 Padding 由样式
 *          StaticResource 提供，实例级资源无法覆盖，只能在模板实例化后经视觉树以本地值归零。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>设置页的媒体库与音乐库。</summary>
public sealed partial class SettingsPage
{
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

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        // Windows App SDK 的 Microsoft.Windows.Storage.Pickers 原生支持非打包应用，
        // 构造时传入 WindowId 即完成归属，无需 InitializeWithWindow 关联句柄。
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
}
