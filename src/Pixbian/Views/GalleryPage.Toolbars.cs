/**
 * 图库页代码后置——工具栏菜单（partial）。
 * 职责：排序/筛选/显示与大小三个下拉菜单的重建与点击、工具栏「更多」与选择「更多」菜单
 *      的自适应补齐、幻灯片放映入口、删除通知条的取消与关闭。
 * 复用约定：菜单每次 Opening 时按当前状态整棵重建（RebuildMenu），勾选态天然最新，
 *          无需维护菜单项引用反向同步；视图与尺寸变更统一经 _shell.SaveSettingsAsync
 *          持久化并广播，本页面不直接写设置。
 * 关键约束：被收起的命令由「更多」菜单按各按钮当前 Visibility 补齐；带下拉的按钮以同名
 *          子菜单提供同源内容；排序菜单在随机依据下两项方向置灰（无效果的重新加载）。
 */

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Models;

namespace Pixbian.Views;

/// <summary>图库页的工具栏菜单。</summary>
public sealed partial class GalleryPage
{
    private const string SortKeyGroupName = "SortKey";
    private const string SortDirectionGroupName = "SortDirection";
    private const string KindGroupName = "Kind";
    private const string LayoutGroupName = "Layout";
    private const string SizeGroupName = "Size";

    private const string SortGlyph = "\uE174";
    private const string FilterGlyph = "\uE71C";
    private const string ViewModeGlyph = "\uECA5";
    private const string SlideShowGlyph = "\uE786";
    private const string SelectGlyph = "\uE73A";
    private const string SelectAllGlyph = "\uE8B3";
    private const string ClearGlyph = "\uE711";

    /// <summary>排序依据项：键、文本、图标。菜单每次打开都据此重建。</summary>
    private static readonly (MediaSortKey Key, string Text, string Glyph)[] SortKeyItems =
    [
        (MediaSortKey.Random, "随机", "\uE8B1"),
        (MediaSortKey.ModifiedDate, "日期", "\uE787"),
        (MediaSortKey.FileSize, "大小", "\uE8A5"),
        (MediaSortKey.FileName, "名字", "\uE8C1")
    ];

    /// <summary>排序方向项：方向、文本、图标。</summary>
    private static readonly (SortDirection Direction, string Text, string Glyph)[] SortDirectionItems =
    [
        (SortDirection.Ascending, "升序", "\uE74A"),
        (SortDirection.Descending, "降序", "\uE74B")
    ];

    /// <summary>类型筛选项：类型（null 为全部）、文本、图标。</summary>
    private static readonly (MediaKind? Kind, string Text, string Glyph)[] KindItems =
    [
        (null, "所有媒体", "\uE8A9"),
        (MediaKind.Image, "照片", "\uE91B"),
        (MediaKind.Video, "视频", "\uE8B2")
    ];

    private static readonly (GalleryViewMode Mode, string Text, string Glyph)[] LayoutItems =
    [
        (GalleryViewMode.Justified, "等高", "\uECA5"),
        (GalleryViewMode.Grid, "方形", "\uF0E2")
    ];

    /// <summary>尺寸档位的文本与图标，按 ThumbnailSizes.Presets 顺序配对（Zip 以短者为准）。</summary>
    private static readonly (string Text, string Glyph)[] SizeLabels =
    [
        ("小", "\uF232"),
        ("中", "\uF57C"),
        ("大", "\uE71A")
    ];

    /// <summary>尺寸档位项：档位值取自 ThumbnailSizes.Presets，保证与 Normalize 的取值域一致。</summary>
    private static IEnumerable<(int Size, string Text, string Glyph)> SizeItems =>
        ThumbnailSizes.Presets.Zip(SizeLabels, (size, label) => (size, label.Text, label.Glyph));

    private MediaSortKey _sortKey = MediaSortKey.ModifiedDate;
    private SortDirection _sortDirection = SortDirection.Descending;

    /// <summary>从选中项（或首个条目）开始幻灯片放映：由外壳打开放映窗口接管；
    /// 视频是否参与放映由设置决定，起点为视频且被过滤时放映视图模型会回落到首个条目。</summary>
    private async void OnSlideShowClick(object sender, RoutedEventArgs e)
    {
        var start = Selection.Count > 0
            ? Selection[0]
            : (ViewModel.Items.Count > 0 ? ViewModel.Items[0] : null);

        if (start is null)
        {
            return;
        }

        await Owner.OpenSlideShowAsync(ViewModel.Items, start);
    }

    /// <summary>排序依据菜单点击：沿用当前方向重新加载。</summary>
    private async void OnSortKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            || !Enum.TryParse<MediaSortKey>(tag, out var key))
        {
            return;
        }

        _sortKey = key;

        await ViewModel.ApplySortOrderAsync(key, _sortDirection);
    }

    /// <summary>升降序菜单点击：沿用当前排序依据重新加载。</summary>
    private async void OnSortDirectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            || !Enum.TryParse<SortDirection>(tag, out var direction))
        {
            return;
        }

        _sortDirection = direction;

        await ViewModel.ApplySortOrderAsync(_sortKey, direction);
    }

    private async void OnKindFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { IsChecked: true, Tag: string tag })
        {
            return;
        }

        var kind = tag switch
        {
            "Image" => MediaKind.Image,
            "Video" => MediaKind.Video,
            _ => (MediaKind?)null
        };

        await ViewModel.ApplyKindFilterAsync(kind);
    }

    /// <summary>布局菜单点击：经外壳持久化视图模式并广播应用，值未变化时跳过。</summary>
    private async void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            && Enum.TryParse(tag, out GalleryViewMode mode)
            && mode != _shell.Settings.ViewMode)
        {
            await _shell.SaveSettingsAsync(_shell.Settings with { ViewMode = mode });
        }
    }

    /// <summary>尺寸菜单点击：经外壳持久化缩略图尺寸并广播应用，值未变化时跳过。</summary>
    private async void OnSizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { IsChecked: true, Tag: string tag }
            && int.TryParse(tag, out var size)
            && size != _shell.Settings.ThumbnailSize)
        {
            await _shell.SaveSettingsAsync(_shell.Settings with { ThumbnailSize = size });
        }
    }

    /// <summary>构建排序菜单项：依据 + 分隔线 + 方向。</summary>
    /// <remarks>随机排序无方向语义，两项置灰，以免产生无效果的重新加载。</remarks>
    private void BuildSortItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (key, text, glyph) in SortKeyItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = SortKeyGroupName,
                IsChecked = key == _sortKey,
                Tag = key.ToString(),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnSortKeyClick;
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());

        var hasDirection = _sortKey != MediaSortKey.Random;

        foreach (var (direction, text, glyph) in SortDirectionItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = SortDirectionGroupName,
                IsChecked = direction == _sortDirection,
                IsEnabled = hasDirection,
                Tag = direction.ToString(),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnSortDirectionClick;
            items.Add(item);
        }
    }

    /// <summary>构建类型筛选菜单项。</summary>
    private void BuildKindItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (kind, text, glyph) in KindItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = KindGroupName,
                IsChecked = kind == ViewModel.KindFilter,
                Tag = kind?.ToString() ?? "All",
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnKindFilterClick;
            items.Add(item);
        }
    }

    /// <summary>构建显示与大小菜单项：布局 + 分隔线 + 尺寸档位。</summary>
    private void BuildViewItems(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (mode, text, glyph) in LayoutItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = LayoutGroupName,
                IsChecked = mode == _shell.Settings.ViewMode,
                Tag = mode.ToString(),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnLayoutClick;
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());

        foreach (var (size, text, glyph) in SizeItems)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = SizeGroupName,
                IsChecked = size == _shell.Settings.ThumbnailSize,
                Tag = size.ToString(CultureInfo.InvariantCulture),
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };

            item.Click += OnSizeClick;
            items.Add(item);
        }
    }

    private void OnSortMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            RebuildMenu(flyout, BuildSortItems);
        }
    }

    private void OnKindMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            RebuildMenu(flyout, BuildKindItems);
        }
    }

    private void OnViewMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            RebuildMenu(flyout, BuildViewItems);
        }
    }

    /// <summary>「更多」菜单打开时重建：先放被收起的命令，再放全选 / 取消选择。</summary>
    private void OnMoreMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Items.Clear();

        if (SelectButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateMenuItem("选择", SelectGlyph, OnSelectClick));
        }

        if (SlideShowButton.Visibility == Visibility.Collapsed)
        {
            var play = CreateMenuItem("幻灯片放映", SlideShowGlyph, OnSlideShowClick);
            play.IsEnabled = HasItems;
            flyout.Items.Add(play);
        }

        // 带下拉菜单的按钮收起后，以同名子菜单提供，内容与按钮上的菜单完全同源。
        if (SortButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateSubMenu("排序", SortGlyph, BuildSortItems));
        }

        if (FilterButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateSubMenu("筛选", FilterGlyph, BuildKindItems));
        }

        if (ViewModeButton.Visibility == Visibility.Collapsed)
        {
            flyout.Items.Add(CreateSubMenu("显示和大小", ViewModeGlyph, BuildViewItems));
        }

        // 宽度足够时没有收起任何按钮，分隔线只会在菜单顶部留下一段突兀的空白。
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        flyout.Items.Add(CreateMenuItem("全选", SelectAllGlyph, OnSelectAllClick, "Ctrl+A"));
        flyout.Items.Add(CreateMenuItem("不选择任何项目", ClearGlyph, OnSelectNoneClick, "Esc, Ctrl+D"));
    }

    /// <summary>选择工具栏「更多」菜单打开时重建：先放被收起的命令（当前只有「播放」），
    /// 再放全选 / 不选择任何项目。</summary>
    private void OnSelectionMoreMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Items.Clear();

        if (SelectionPlayButton.Visibility == Visibility.Collapsed)
        {
            var play = CreateMenuItem("播放", SlideShowGlyph, OnSlideShowClick);
            play.IsEnabled = HasSelection;
            flyout.Items.Add(play);
        }

        // 宽度足够时没有收起任何命令，分隔线只会在菜单顶部留下一段突兀的空白。
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        flyout.Items.Add(CreateMenuItem("全选", SelectAllGlyph, OnSelectAllClick, "Ctrl+A"));
        flyout.Items.Add(CreateMenuItem("不选择任何项目", ClearGlyph, OnSelectNoneClick, "Esc, Ctrl+D"));
    }

    /// <summary>取消正在进行的删除：已移入回收站的部分保留。</summary>
    private void OnCancelDeleteClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelDelete();
    }

    /// <summary>手动关闭删除结果通知条。</summary>
    private void OnCloseDeleteResultClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseDeleteResult();
    }

    /// <summary>清空并重建菜单内容；每次打开都重建，勾选与可用态按当前状态生成，无需反向同步。</summary>
    private static void RebuildMenu(MenuFlyout flyout, Action<IList<MenuFlyoutItemBase>> build)
    {
        flyout.Items.Clear();
        build(flyout.Items);
    }

    private static MenuFlyoutItem CreateMenuItem(
        string text,
        string glyph,
        RoutedEventHandler handler,
        string? acceleratorText = null)
    {
        var item = new MenuFlyoutItem
        {
            Icon = new FontIcon { Glyph = glyph },
            KeyboardAcceleratorTextOverride = acceleratorText,
            Text = text
        };

        item.Click += handler;

        return item;
    }

    private static MenuFlyoutSubItem CreateSubMenu(
        string text,
        string glyph,
        Action<IList<MenuFlyoutItemBase>> build)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Icon = new FontIcon { Glyph = glyph },
            Text = text
        };

        build(subItem.Items);

        return subItem;
    }
}
