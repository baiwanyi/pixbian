/**
 * 主窗口代码后置——导航（partial）。
 * 职责：NavigationView 分组展开交互（点行只导航、点箭头才折叠）、导航项选中分派
 *      （根目标 / 媒体文件夹 / 分类 / 收藏分组）、页面装载、搜索输入下发与设置页跳转。
 * 复用约定：页面实例与视图模型均由依赖注入提供；过滤条件一律委托 GalleryViewModel 的
 *          Apply* 方法，本文件不写查询。
 * 关键约束：回调内同步改 NavigationViewItem.IsExpanded 会重入控件展开逻辑并使进程
 *          fail-fast——改写一律经 TryEnqueue 延后一拍；ItemInvoked 无法区分点行与点箭头，
 *          只能按 PointerPressed 落点判定（箭头在行右端约 44px 内）。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>主窗口的导航交互与跳转。</summary>
public sealed partial class MainWindow
{
    /// <summary>图库行右侧展开箭头的可点宽度：箭头位于行右端，此为自右边缘起算的命中范围。</summary>
    private const double GalleryChevronHitWidth = 44;

    private NavigationTarget _currentTarget = NavigationTarget.AllPhotos;

    /// <summary>当前按分类过滤的主键；刷新分类完成后据此重放过滤，非分类过滤上下文为 null。</summary>
    private long? _activeCategoryFilter;

    /// <summary>图库分组的展开状态：只由右侧展开箭头改变，点行本身导航时不改。</summary>
    private bool _isGalleryExpanded = true;

    /// <summary>图库分组最近一次展开/折叠之前的状态：点行触发的切换要按它还原。</summary>
    private bool _isGalleryExpandedBeforeToggle = true;

    /// <summary>程序自行改写图库展开状态期间为 true：区别于用户点箭头，不记入 _isGalleryExpanded。</summary>
    private bool _isSyncingGalleryExpansion;

    /// <summary>本次点击落在图库行本身而非右侧箭头上：其引发的展开/折叠需要撤销。</summary>
    private bool _isGalleryContentClick;

    /// <summary>本次按下落在图库行右侧的展开箭头区：允许切换展开，且不算「点行」。</summary>
    private bool _isGalleryChevronClick;

    /// <summary>有分组被展开：转交判定，仅图库分组且非点行引发时才记为设定状态。</summary>
    private void OnNavigationItemExpanding(
        NavigationView sender,
        NavigationViewItemExpandingEventArgs args) =>
        TrackGalleryExpansion(args.ExpandingItemContainer, true);

    /// <summary>有分组被折叠：转交判定，仅图库分组且非点行引发时才记为设定状态。</summary>
    private void OnNavigationItemCollapsed(
        NavigationView sender,
        NavigationViewItemCollapsedEventArgs args) =>
        TrackGalleryExpansion(args.CollapsedItemContainer, false);

    /// <summary>记录本次按下是否落在图库行右侧的展开箭头区，供展开切换判定来源。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnGalleryPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(GalleryNavItem).Position;
        _isGalleryChevronClick = GalleryNavItem.ActualWidth - position.X <= GalleryChevronHitWidth;
    }

    /// <summary>记录图库分组的展开/折叠；点行本身引发的切换当场撤销，只保留箭头设定的状态。</summary>
    /// <param name="container">发生展开/折叠的项容器。</param>
    /// <param name="expanded">true 为展开，false 为折叠。</param>
    private void TrackGalleryExpansion(object? container, bool expanded)
    {
        if (_isSyncingGalleryExpansion || !ReferenceEquals(container, GalleryNavItem))
        {
            return;
        }

        // 点行引发的切换（标记由 ItemInvoked 置起，切换可能在其前也可能在其后）：
        // 立刻撤销回切换前的状态；点箭头引发的切换走下面的记录分支。
        if (_isGalleryContentClick && !_isGalleryChevronClick)
        {
            _isGalleryContentClick = false;
            RestoreGalleryExpansion(!expanded);
            return;
        }

        _isGalleryExpandedBeforeToggle = _isGalleryExpanded;
        _isGalleryExpanded = expanded;
    }

    /// <summary>
    /// 点击图库行本身只导航到图库：控件默认会把「点内容」也当作展开/折叠切换，
    /// 这里把切换撤销回原状态。
    /// </summary>
    /// <param name="sender">导航控件。</param>
    /// <param name="args">调用参数，含被点击项的容器。</param>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!ReferenceEquals(args.InvokedItemContainer, GalleryNavItem) || _isGalleryChevronClick)
        {
            return;
        }

        // 切换可能已经发生，也可能紧随其后，两条路径都要覆盖：先按当前状态还原一次，
        // 再把标记留到切换回调中消费；标记在下一个消息清除，避免污染后续交互。
        _isGalleryContentClick = true;
        RestoreGalleryExpansion(_isGalleryExpandedBeforeToggle);

        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
            () => _isGalleryContentClick = false);
    }

    /// <summary>把图库分组的展开状态改回指定值；状态已是该值时不重算子项。</summary>
    /// <param name="expanded">true 为展开，false 为折叠。</param>
    private void RestoreGalleryExpansion(bool expanded)
    {
        if (GalleryNavItem.IsExpanded == expanded)
        {
            return;
        }

        // 关键约束：改写必须延到下一个消息。展开/折叠是控件处理点击时同步推进的，
        // 在回调内改 IsExpanded 会重入其展开逻辑并使进程 fail-fast 退出（无托管异常、无日志）。
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            _isSyncingGalleryExpansion = true;
            _isGalleryExpanded = expanded;
            GalleryNavItem.IsExpanded = expanded;
            _isSyncingGalleryExpansion = false;
        });
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

        if (tag.StartsWith(FavoriteGroupTagPrefix, StringComparison.Ordinal))
        {
            SelectFavoriteGroup(tag);
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

        // Videos 不在此下发筛选：该目标已由 ShortPage 承载，向图库视图模型下发
        // “仅视频”只会让结果落在当前不可见的页面上，且回到图库时还要再重置一次。
        switch (target)
        {
            case NavigationTarget.AllPhotos:
                _ = _gallery.ApplyNavigationFilterAsync(null, onlyFavorites: false);
                break;

            case NavigationTarget.Favorites:
                _ = _gallery.ApplyNavigationFilterAsync(null, onlyFavorites: true);
                ExpandFavoriteGroups();
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

        // 过滤完成后第一页数据已就绪，直接以图库当前列表为播放列表打开放映窗口。
        var first = _gallery.Items.FirstOrDefault();

        if (first is null)
        {
            await ShowInfoDialogAsync("该文件夹暂无可放映的媒体。");
            return;
        }

        await OpenSlideShowAsync(_gallery.Items, first);
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

    /// <summary>选中收藏分组子项：切到图库页并按该分组过滤；主键为 0 时取「未分组」的收藏条目。</summary>
    /// <param name="tag">子项标记，形如 favgroup:3。</param>
    private void SelectFavoriteGroup(string tag)
    {
        if (!long.TryParse(tag.AsSpan(FavoriteGroupTagPrefix.Length), out var groupId))
        {
            return;
        }

        var name = groupId == UngroupedGroupId
            ? UngroupedGroupName
            : _favoriteGroups.Groups.FirstOrDefault(g => g.Id == groupId)?.Name;

        // 分组已被删除（子项尚未重建）时回落到收藏夹根视图，避免停在空标题上。
        if (name is null)
        {
            NavigateToTarget(NavigationTarget.Favorites);
            return;
        }

        CloseViewerIfVisible();
        _activeCategoryFilter = null;
        _currentTarget = NavigationTarget.Favorites;
        NotifyTargetChanged();
        ShowPage(_galleryPage);
        OnPropertyChanged(nameof(SearchPlaceholder));

        _ = groupId == UngroupedGroupId
            ? _gallery.ApplyUngroupedFavoritesFilterAsync(name)
            : _gallery.ApplyFavoriteGroupFilterAsync(groupId, name);
    }

    /// <summary>进入收藏内容后展开分组子项。</summary>
    /// <remarks>改写一律延后一拍：展开/折叠由控件在处理点击时同步推进，
    ///          在导航回调内直接改 IsExpanded 会重入其展开逻辑并使进程 fail-fast。</remarks>
    private void ExpandFavoriteGroups() =>
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (!FavoritesNavItem.IsExpanded)
            {
                FavoritesNavItem.IsExpanded = true;
            }
        });

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
                ShowPage(_shortPage);
                break;

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
}
