/**
 * 设置页代码后置——收藏分组管理（partial）。
 * 职责：分组的列表选中与改名模式互斥显隐、新增 / 改名 / 删除与选中态清理。
 * 复用约定：分组集合与编辑态统一由 FavoriteGroupViewModel 承担（与主窗口侧栏、图库页
 *          共享同一实例，改动即时同步）。
 * 关键约束：选中项直接读控件 SelectedItem 而非绑定回写的页面属性（x:Bind TwoWay 的
 *          回写时序不保证早于 SelectionChanged）；新增名称先经代码后置回写 VM，
 *          避免 TextBox 失焦才更新源导致校验取到旧值；集合整体重建后必须清空选中，
 *          否则改名区会停留在已删除的分组上。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Models;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>设置页的收藏分组管理。</summary>
public sealed partial class SettingsPage
{
    private FavoriteGroup? _selectedGroup;

    /// <summary>列表中被选中的分组；非空时下方输入区切换为改名模式。</summary>
    public FavoriteGroup? SelectedGroup
    {
        get => _selectedGroup;
        set => SetField(ref _selectedGroup, value);
    }

    /// <summary>是否处于分组改名状态；驱动新增区与改名区的互斥显隐。</summary>
    public bool IsEditingGroup => SelectedGroup is not null;

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
}
