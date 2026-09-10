/**
 * 主窗口代码后置——导航项列表与扫描源操作（partial）。
 * 职责：左栏「图库 / 分类 / 收藏夹」动态子项的重建与选中恢复、扫描源右键菜单
 *      （创建 / 重命名 / 放映 / 资源管理器 / 移除 / 删除）、Ctrl+I 添加媒体文件夹，
 *      以及扫描源目录可用性刷新（窗口激活时复查，失效项灰显禁用并换错误徽章）。
 * 复用约定：目录存在性判定统一经 ApplyFolderAvailability 应用到子项，重建与刷新两条路径共用；
 *          探测在线程池执行，结果回 UI 线程写入。
 * 复用约定：增删改一律委托 SettingsViewModel 的 Command（内部完成磁盘操作与索引同步）；
 *          子项标记按前缀 + 主键构造，选中态按 Tag 恢复，不依赖菜单项排列顺序。
 * 关键约束：NavigationView 把层级子项扁平进同一列表且只在 IsExpanded 值变化时重算——
 *          重建前必须先收起、重建后恢复展开状态，否则新子项不会进入列表；
 *          explorer 路径先去除结尾分隔符再引号包裹，argv 形式杜绝命令注入。
 */

using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using Pixbian.Core.Utilities;
using Pixbian.ViewModels;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;

namespace Pixbian.Views;

/// <summary>主窗口的导航项列表与扫描源操作。</summary>
public sealed partial class MainWindow
{
    /// <summary>图库分组子项 Tag 前缀，后跟扫描源主键。</summary>
    private const string MediaFolderTagPrefix = "media-folder:";

    /// <summary>分类分组子项 Tag 前缀，后跟分类主键。</summary>
    private const string CategoryTagPrefix = "category:";

    /// <summary>收藏分组子项 Tag 前缀，后跟分组主键；主键 0 为「未分组」。</summary>
    private const string FavoriteGroupTagPrefix = "favgroup:";

    /// <summary>「未分组」子项的 Tag：收藏但不属任何分组的条目。</summary>
    private const string UngroupedFavoritesTag = "favgroup:0";

    /// <summary>「未分组」的分组主键值；真实分组主键自增从 1 起，0 不会与之冲突。</summary>
    private const long UngroupedGroupId = 0;

    /// <summary>「未分组」子项的展示名。</summary>
    private const string UngroupedGroupName = "未分组";

    /// <summary>幻灯片菜单项字形，与图库页工具栏按钮同源（SlideShowGlyph）。</summary>
    private const string SlideShowGlyph = "\uE786";

    /// <summary>空心文件夹字形：Segoe Fluent Icons 的 E8B7 是实心 FolderFill，ED25 在两代字体下均为空心斜开盖文件夹，左栏子项与图库页头共用。</summary>
    private const string FolderGlyph = "\uED25";

    /// <summary>失效扫描源字形（EA39 ErrorBadge）：目录已不存在时替代文件夹图标，明示该项不可用。</summary>
    private const string MissingFolderGlyph = "\uEA39";

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

        // 路径来自受信任的索引数据，经 argv 形式传入并引号包裹，杜绝命令注入；
        // 结尾分隔符必须去掉：`"D:\Lib\"` 中 `\` 会转义收尾引号，explorer 解析到裸路径。
        var target = row.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"")
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
            AppLog.Error("Gallery", "索引完成后刷新图库失败。", ex);
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

    /// <summary>收藏分组集合变化后重建收藏夹子项。</summary>
    private void OnFavoriteGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshFavoriteGroupItems();

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
                Tag = $"{MediaFolderTagPrefix}{folder.Folder.Id}"
            };

            ApplyFolderAvailability(item, folder.Path, Directory.Exists(folder.Path));
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

    /// <summary>把子项的图标、禁用态与提示同步为扫描源目录的实际可用性。</summary>
    /// <param name="item">图库子项。</param>
    /// <param name="path">扫描源目录路径。</param>
    /// <param name="isAvailable">目录当前是否存在。</param>
    /// <remarks>不可用项禁用后即不可点、不可选中；图标换成错误徽章，提示里保留完整路径便于排查。</remarks>
    private static void ApplyFolderAvailability(NavigationViewItem item, string path, bool isAvailable)
    {
        item.IsEnabled = isAvailable;
        item.Icon = new FontIcon { Glyph = isAvailable ? FolderGlyph : MissingFolderGlyph };
        ToolTipService.SetToolTip(item, isAvailable ? path : $"文件夹不存在或不可访问：{path}");
    }

    /// <summary>刷新全部图库子项的可用性（不重建列表）：目录探测在线程池执行，结果回 UI 线程应用。</summary>
    /// <remarks>
    /// 触发时机取窗口激活：外接盘拔出、网络盘断开都属「切走再切回」时才可能被察觉的变化。
    /// 探测必须离开 UI 线程——网络盘或休眠机械盘上的 Directory.Exists 可能阻塞数百毫秒。
    /// 不重建列表：重建会打断当前选中并触发一次完整子项刷新，而这里只改图标与禁用态。
    /// </remarks>
    private async Task RefreshFolderAvailabilityAsync()
    {
        var folders = _settings.Folders.ToList();

        if (folders.Count == 0)
        {
            return;
        }

        var availability = await Task.Run(() =>
            folders.Select(folder => Directory.Exists(folder.Path)).ToList());

        var unavailablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];

            if (!availability[i])
            {
                unavailablePaths.Add(folder.Path);
            }

            if (FindNavItem(GalleryNavItem.MenuItems, $"{MediaFolderTagPrefix}{folder.Folder.Id}") is { } item)
            {
                ApplyFolderAvailability(item, folder.Path, availability[i]);
            }
        }

        FallbackIfActiveFolderUnavailable(unavailablePaths);
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

    /// <summary>按当前收藏分组重建收藏夹子项；被移除的分组若正被选中，回落到收藏夹根视图。</summary>
    private void RefreshFavoriteGroupItems()
    {
        var selectedTag = (NavigationViewControl.SelectedItem as NavigationViewItem)?.Tag as string;

        // 先收起再重建：NavigationView 把层级子项扁平进同一个列表，且只在 IsExpanded
        // 变化时重算，状态不变（哪怕子项是后加的）就不会把新子项插进列表。
        var wasExpanded = FavoritesNavItem.IsExpanded;
        FavoritesNavItem.IsExpanded = false;

        FavoritesNavItem.MenuItems.Clear();

        foreach (var group in _favoriteGroups.Groups)
        {
            FavoritesNavItem.MenuItems.Add(new NavigationViewItem
            {
                Content = group.Name,
                Icon = new FontIcon { Glyph = CategoryFolderGlyph },
                Tag = $"{FavoriteGroupTagPrefix}{group.Id.ToString(CultureInfo.InvariantCulture)}"
            });
        }

        // 未分组排在最后：它是收藏的默认态而非一条分组记录，且不来自分组集合；
        // 放在自建分组之后，避免「未分组」这一兜底项把用户真正关心的分组挤到后面。
        FavoritesNavItem.MenuItems.Add(new NavigationViewItem
        {
            Content = UngroupedGroupName,
            Icon = new FontIcon { Glyph = CategoryFolderGlyph },
            Tag = UngroupedFavoritesTag
        });

        FavoritesNavItem.IsExpanded = wasExpanded;

        if (selectedTag?.StartsWith(FavoriteGroupTagPrefix, StringComparison.Ordinal) == true
            && FindNavItem(NavigationViewControl.MenuItems, selectedTag) is null)
        {
            NavigationViewControl.SelectedItem = FavoritesNavItem;
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
}
