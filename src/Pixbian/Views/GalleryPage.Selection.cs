/**
 * 图库页代码后置——选择模式与收藏分组（partial）。
 * 职责：勾选式选择模式的进出与选择集合聚合（方形 GridView 多选 + 等高选择服务双通道）、
 *      收藏分组下拉菜单的归属勾选、全选/计数、键盘快捷键与焦点还原。
 * 复用约定：批量操作一律取 Selection 快照委托 ViewModel；分组归属复用
 *          ViewModel.ApplySelectionGroupAsync / ApplySelectionUngroupedAsync。
 * 关键约束：切换 SelectionMode 会重置各列表选择——先抓快照再恢复，内部调整期间以
 *          _isRestructuringSelection 抑制自动进出联动；ItemClick 与 SelectionChanged 的
 *          触发时序不定，普通模式单击的「设为当前项」语义靠一次性标志覆盖两条时序。
 */

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pixbian.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace Pixbian.Views;

/// <summary>图库页的选择模式与收藏分组。</summary>
public sealed partial class GalleryPage
{
    private bool _isSelectionMode;
    private bool _hasSelection;

    /// <summary>
    /// 普通模式点击图片主体后置位的一次性标志：ItemClick 先于 SelectionChanged 触发时，
    /// OnItemClick 里的 Clear 清的是尚未加入的空集合、拦不住点击副作用，须由下一次
    /// SelectionChanged 消费本标志跳过自动进入并清除选择。点击复选框不触发 ItemClick，不受影响。
    /// </summary>
    private bool _suppressAutoEnterOnce;

    /// <summary>模式切换内部调整选择集合期间为 true，抑制 SelectionChanged 的自动进出联动。</summary>
    private bool _isRestructuringSelection;

    /// <summary>正在按数据重建菜单勾选态；期间 CheckBox 触发的 Checked / Unchecked 一律忽略，
    /// 否则程序化赋值会被当成用户操作回写一遍。</summary>
    private bool _isApplyingGroupSelection;

    /// <summary>收藏下拉菜单本次打开期间是否改动过分组归属；关闭时据此决定是否退出选择模式。</summary>
    private bool _isFavoriteMenuDirty;

    /// <summary>等高视图复选框点击：普通模式先进入选择模式（保留既有选择），随后翻转选中。</summary>
    private void OnJustifiedCheckClick(object sender, RoutedEventArgs e)
    {
        // 与图片主体走同一套命中解析（按 Repeater 索引映射取条目）：复选框的 DataContext
        // 与图片绑定的 DataContext 同源，一旦元素复用残留，两者会一起错位，取哪个都错。
        if (ResolveHit(sender as DependencyObject).Item is not { } item)
        {
            return;
        }

        if (!IsSelectionMode)
        {
            EnterSelectionMode(clearExisting: false);
        }

        ViewModel.JustifiedSelection.Toggle(item);
    }

    /// <summary>等高视图选择集合变化：聚合选中项并驱动选择模式的自动进出与计数。</summary>
    private void OnJustifiedSelectionChanged(object? sender, EventArgs e)
    {
        if (_isRestructuringSelection)
        {
            return;
        }

        Selection = ViewModel.JustifiedSelection.SelectedItems;
        HasSelection = Selection.Count > 0;

        if (!IsSelectionMode && HasSelection)
        {
            EnterSelectionMode(clearExisting: false);
            return;
        }

        if (IsSelectionMode && !HasSelection)
        {
            ExitSelectionMode();
            return;
        }

        UpdateSelectionCount();
    }

    /// <summary>聚合当前视图的选中项：网格视图取主控件，等高视图取选择服务。</summary>
    private List<MediaItemViewModel> CollectSelection()
    {
        if (IsGridView)
        {
            return GridViewControl.SelectedItems.OfType<MediaItemViewModel>().ToList();
        }

        return [.. ViewModel.JustifiedSelection.SelectedItems];
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Selection = CollectSelection();
        HasSelection = Selection.Count > 0;

        // 模式切换内部调整选择集合期间抑制自动进出，防止误触发（见 EnterSelectionMode/ExitSelectionMode）。
        if (_isRestructuringSelection)
        {
            return;
        }

        // 消费一次性标志：本次选择变化源于点击图片主体（ItemClick 先行打标），不得进入选择模式，
        // 并清除点击副作用；清除引发的再次 SelectionChanged 为空选择、无分支命中，稳定收敛。
        var clickedItemBody = _suppressAutoEnterOnce;
        _suppressAutoEnterOnce = false;

        // 普通模式下仅 hover 复选框勾选（不触发 ItemClick）才进入选择模式（照片应用式交互）。
        if (!IsSelectionMode && HasSelection)
        {
            if (clickedItemBody && sender is GridView grid)
            {
                grid.SelectedItems.Clear();
                return;
            }

            EnterSelectionMode(clearExisting: false);
            return;
        }

        // 选择模式下取消了所有选中：自动退出选择模式（逐个取消勾选、清空快捷键、
        // 删除完最后一个选中项，均经此路径退出）。
        if (IsSelectionMode && !HasSelection)
        {
            ExitSelectionMode();
            return;
        }

        UpdateSelectionCount();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        // 勾选式选择模式下，单击仅用于切换选中态，由 GridView 多选机制处理，不记录为当前项。
        if (IsSelectionMode)
        {
            return;
        }

        if (e.ClickedItem is MediaItemViewModel item)
        {
            ViewModel.SelectedItem = item;

            // Extended 模式单击图片主体会顺手把条目加入选择集合。普通模式单击的语义是「设为当前项」，
            // 选择集合必须保持为空。事件时序不定：ItemClick 在前则此处 Clear 无效，须由
            // _suppressAutoEnterOnce 让下一次 SelectionChanged 拦截（见 OnSelectionChanged）；
            // ItemClick 在后则此处 Clear 直接生效。两路并存覆盖所有时序。
            _suppressAutoEnterOnce = true;
            if (sender is GridView grid)
            {
                grid.SelectedItems.Clear();
            }
        }
    }

    /// <summary>下拉菜单「仅加入收藏（不分组）」：清掉全部分组归属并置收藏，语义与未分组一致；
    /// 这是终结性操作，收工即关闭菜单（关闭时统一退出选择模式）。</summary>
    private async void OnAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        await ViewModel.ApplySelectionUngroupedAsync(Selection);

        dispatcher.TryEnqueue(() =>
        {
            _isFavoriteMenuDirty = true;
            RefreshGroupCounts();
            FavoriteGroupFlyout.Hide();
        });
    }

    /// <summary>收藏菜单关闭：本次打开期间改过归属就退出选择模式。</summary>
    /// <remarks>勾选分组时菜单不自动关闭（可连勾多个组），故退出推迟到关闭时，
    ///          避免勾完第一个分组就打断操作；未做任何改动时关闭不退出，保留现场。</remarks>
    private void OnFavoriteGroupFlyoutClosed(object sender, object e)
    {
        if (!_isFavoriteMenuDirty)
        {
            return;
        }

        _isFavoriteMenuDirty = false;
        ExitSelectionMode();
    }

    /// <summary>收藏下拉菜单打开：按选中项的实际分组归属重建勾选态。</summary>
    private async void OnFavoriteGroupFlyoutOpening(object sender, object e)
    {
        _isFavoriteMenuDirty = false;

        var selection = Selection;

        if (selection.Count == 0)
        {
            return;
        }

        var byMedia = await FavoriteGroups.GetGroupIdsByMediaAsync(selection.Select(i => i.Id).ToList());

        _isApplyingGroupSelection = true;

        try
        {
            GroupOptions.Clear();

            foreach (var group in FavoriteGroups.Groups)
            {
                var members = byMedia.Values.Count(ids => ids.Contains(group.Id));

                GroupOptions.Add(new FavoriteGroupOption
                {
                    GroupId = group.Id,
                    Name = group.Name,
                    IsChecked = members == selection.Count
                });
            }
        }
        finally
        {
            _isApplyingGroupSelection = false;
        }
    }

    /// <summary>勾选分组：把选中条目加入该组（隐含置收藏）。分组之间互不排斥，可连续勾多个。</summary>
    private async void OnFavoriteGroupChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingGroupSelection || sender is not CheckBox { Tag: FavoriteGroupOption option })
        {
            return;
        }

        // 仓储内部 ConfigureAwait(false)，await 之后不在 UI 线程，队列须提前捕获。
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        await ViewModel.ApplySelectionGroupAsync(Selection, option.GroupId, isMember: true);

        dispatcher.TryEnqueue(() =>
        {
            _isFavoriteMenuDirty = true;
            RefreshGroupCounts();
        });
    }

    /// <summary>取消勾选分组：把选中条目移出该组，不动收藏状态。</summary>
    private async void OnFavoriteGroupUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingGroupSelection || sender is not CheckBox { Tag: FavoriteGroupOption option })
        {
            return;
        }

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        await ViewModel.ApplySelectionGroupAsync(Selection, option.GroupId, isMember: false);

        dispatcher.TryEnqueue(() =>
        {
            _isFavoriteMenuDirty = true;
            RefreshGroupCounts();
        });
    }

    /// <summary>刷新分组集合：成员数变化后侧栏子项与设置页计数才跟得上。</summary>
    private void RefreshGroupCounts() => _ = FavoriteGroups.LoadAsync();

    /// <summary>选择工具栏「删除」：把选中项移入回收站，进度与结果经底部通知条呈现。</summary>
    private async void OnDeleteSelectionClick(object sender, RoutedEventArgs e)
    {
        await DeleteContextItemsAsync(Selection);
    }

    /// <summary>点击「选择」按钮：进入勾选式选择模式，所有列表切换为多选并清空既有选择。</summary>
    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        EnterSelectionMode(clearExisting: true);
    }

    /// <summary>进入勾选式选择模式，两个视图的所有列表切换为多选。</summary>
    /// <param name="clearExisting">true 清空既有选择（工具栏「选择」入口）；false 保留（条目复选框入口）。</param>
    /// <remarks>切换 SelectionMode 会重置各列表的选择，故先抓快照，切换归零后再按需恢复，
    /// 保证复选框入口勾选的第一项不丢。内部调整选择须抑制 SelectionChanged 的自动退出。</remarks>
    private void EnterSelectionMode(bool clearExisting)
    {
        var mainSelection = GridViewControl.SelectedItems.OfType<object>().ToList();

        _isRestructuringSelection = true;

        try
        {
            IsSelectionMode = true;

            GridViewControl.SelectionMode = ListViewSelectionMode.Multiple;

            // 切换 SelectionMode 可能已清空选择，统一归零后按快照恢复，避免重复添加。
            GridViewControl.SelectedItems.Clear();

            if (!clearExisting)
            {
                foreach (var item in mainSelection)
                {
                    GridViewControl.SelectedItems.Add(item);
                }
            }
            else
            {
                // 工具栏「选择」入口清空全部视图的既有选择（等高视图选择在服务内）。
                ViewModel.JustifiedSelection.Clear();
            }

            SyncJustifiedCheckVisibility();
            UpdateSelectionCount();
        }
        finally
        {
            _isRestructuringSelection = false;
        }

        // 首次进入选择模式会引发一轮布局风暴（全量可见条目的复选框显示、页头整行替换），
        // 程序化焦点若同帧执行会叠加焦点遍历与同步布局造成可感卡顿；延后一帧错峰。
        DispatcherQueue.TryEnqueue(() => RestoreContentFocus());
    }

    /// <summary>退出选择模式并清空选择；工具栏「取消」与「取消所有选中」的自动退出共用。</summary>
    private void ExitSelectionMode()
    {
        _isRestructuringSelection = true;

        try
        {
            IsSelectionMode = false;

            GridViewControl.SelectionMode = ListViewSelectionMode.Extended;
            GridViewControl.SelectedItems.Clear();
            ViewModel.JustifiedSelection.Clear();

            // 兜底复位：已回收但残留的 Repeater 元素上可能还绑着历史条目的选中态，选择服务
            // 清不到它们（既不在 _selected 也不在当前集合），退出时按当前集合全量复位，
            // 否则悬停显示复选框时还能看到不属于本次选择的勾。
            foreach (var item in ViewModel.Items)
            {
                item.IsSelected = false;
            }

            SyncJustifiedCheckVisibility();

            Selection = [];
            HasSelection = false;
            SelectionCountText = "选择项目";
        }
        finally
        {
            _isRestructuringSelection = false;
        }

        RestoreContentFocus();
    }

    /// <summary>把选择模式的复选框可见性批量同步到全部条目（等高 Repeater 模板由条目属性驱动）。</summary>
    private void SyncJustifiedCheckVisibility()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsSelectionCheckVisible = IsSelectionMode;
        }

        // 模式切换会改写复选框的绑定值（Opacity / IsHitTestVisible），悬停态写下的元素本地值
        // 随之失效，丢弃悬停引用即可；不要在这里回写本地值，那会压住下一次绑定推送。
        _hoveredElement = null;
    }

    /// <summary>点击「取消」：退出选择模式并清空选择。</summary>
    private void OnCancelSelectionClick(object sender, RoutedEventArgs e)
    {
        ExitSelectionMode();
    }

    /// <summary>把焦点设回内容区列表（仅在焦点已丢失或落到页面之外时）。</summary>
    /// <remarks>
    /// 触发模式切换的按钮随所在行整体隐藏、从视觉树卸载，框架会把焦点自动转移到
    /// Tab 序中下一个可聚焦控件（搜索框）；焦点落在文本框后，Delete 等按键会被当作
    /// 文本编辑消费，页面 KeyDown 收不到——只有这种情况才需要还原焦点。
    /// 焦点仍在本页视觉树内（如用户刚点过的条目复选框）时**不抢**：
    /// 每次模式切换 / 删除都把焦点设到列表框，会打断用户的键盘位置感。
    /// </remarks>
    private void RestoreContentFocus()
    {
        if (IsFocusWithinPage())
        {
            return;
        }

        // 等高视图焦点给外层 ScrollViewer（Repeater 无内建焦点链，键事件经页面级 KeyDown）。
        // ScrollViewer 默认 IsTabStop=False，Focus 会静默失败（返回 false），焦点就会留在
        // 框架自动转移的搜索框里，DEL 等键全被文本编辑消费——JustifiedView 必须显式
        // IsTabStop=True（见 XAML），此处不检查返回值是有前提的。
        if (IsJustifiedView)
        {
            JustifiedView.Focus(FocusState.Programmatic);
            return;
        }

        GridViewControl.Focus(FocusState.Programmatic);
    }

    /// <summary>最近一次获得焦点的元素：供焦点还原前判定焦点是否仍在本页视觉树内。</summary>
    private DependencyObject? _lastFocusedElement;

    private void OnFocusManagerGotFocus(object? sender, FocusManagerGotFocusEventArgs args)
        => _lastFocusedElement = args.NewFocusedElement;

    /// <summary>最近获焦元素是否仍在本页视觉树内；元素已被卸载（如删除的条目复选框）视为否。</summary>
    private bool IsFocusWithinPage()
    {
        for (var node = _lastFocusedElement; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>全选当前列表所有条目。</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        SelectAll(true);
    }

    /// <summary>取消全选。</summary>
    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        SelectAll(false);
    }

    /// <summary>批量切换选中态：方形视图经 GridView，等高视图经选择服务。</summary>
    /// <param name="select">true 表示全选，false 表示清空。</param>
    private void SelectAll(bool select)
    {
        ToggleAllGrid(GridViewControl, select);

        if (select)
        {
            ViewModel.JustifiedSelection.SelectAll();
        }
        else
        {
            ViewModel.JustifiedSelection.Clear();
        }

        UpdateSelectionCount();
    }

    private static void ToggleAllGrid(GridView grid, bool select)
    {
        var items = grid.Items.Cast<object>().ToList();

        if (select)
        {
            foreach (var item in items)
            {
                grid.SelectedItems.Add(item);
            }
        }
        else
        {
            grid.SelectedItems.Clear();
        }
    }

    /// <summary>刷新选择计数标题：未选时显示模式提示，选中后显示数量。</summary>
    private void UpdateSelectionCount()
    {
        var count = CollectSelection().Count;
        SelectionCountText = count == 0 ? "选择项目" : $"已选择 {count} 个项目";
    }

    /// <summary>键盘快捷键：F5 从头开始幻灯片播放；Ctrl+A 全选；Ctrl+D / Esc 取消选择；
    /// Ctrl+C 复制文件；Delete 移入回收站；F2 重命名；F3 在资源管理器中打开。</summary>
    /// <remarks>
    /// 一律用 KeyDown 处理，不用 XAML 的 Page.KeyboardAccelerators：注册快捷键后，框架会按官方设计
    /// 把按键组合追加到作用域内「所有控件」的 ToolTip 上（MenuFlyoutItem 除外），
    /// 导致 hover 图片时文件名提示里混入「Ctrl+A」。没有 accelerator 就没有该行为。
    /// 作用域为「焦点在本页内」，与项目其他快捷键（Ctrl+C / Delete / F2）一致。
    /// </remarks>
    private void OnGalleryPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F5 && !e.Handled)
        {
            e.Handled = true;
            OnSlideShowClick(this, new RoutedEventArgs());
            return;
        }

        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                is CoreVirtualKeyStates.Down or CoreVirtualKeyStates.Locked)
        {
            if (e.Key == VirtualKey.C && !e.Handled)
            {
                e.Handled = true;
                _ = CopyItemToClipboardAsync(GetContextTarget());
            }

            if (e.Key == VirtualKey.A && !e.Handled)
            {
                e.Handled = true;

                // 非选择模式下条目不显示复选框与遮罩，直接全选不会有任何视觉反馈，故先切模式
                // （OnSelectClick 会清空既有选择，随后再全选，顺序不可颠倒）。
                if (!IsSelectionMode)
                {
                    OnSelectClick(this, new RoutedEventArgs());
                }

                SelectAll(true);
            }

            if (e.Key == VirtualKey.D && !e.Handled)
            {
                e.Handled = true;
                SelectAll(false);
            }

            return;
        }

        if (e.Key == VirtualKey.Escape && !e.Handled)
        {
            e.Handled = true;
            SelectAll(false);
            return;
        }

        if (e.Key == VirtualKey.Delete && !e.Handled)
        {
            e.Handled = true;

            var targets = GetContextTarget();

            _ = DeleteContextItemsAsync(targets);
            return;
        }

        if ((e.Key == VirtualKey.F2 || e.Key == VirtualKey.F3) && !e.Handled)
        {
            var target = GetContextTarget();
            var first = target.Count > 0 ? target[0] : null;
            if (first is null)
            {
                return;
            }

            e.Handled = true;
            if (e.Key == VirtualKey.F2)
            {
                _ = ShowRenameDialogAsync(first);
            }
            else
            {
                OpenInFileExplorer(first.Item.Path);
            }
        }
    }

    /// <summary>取得右键/快捷键操作的目标集合：选择模式下为整组选择，否则为单选当前项。</summary>
    private IReadOnlyList<MediaItemViewModel> GetContextTarget()
    {
        if (IsSelectionMode)
        {
            return Selection;
        }

        return ViewModel.SelectedItem is { } single ? new[] { single } : [];
    }
}
