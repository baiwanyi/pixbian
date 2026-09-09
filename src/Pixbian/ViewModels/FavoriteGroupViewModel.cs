/**
 * 收藏分组管理视图模型。
 * 职责：维护分组集合的加载、新增、行内改名与删除，并代理成员关系的批量写入。
 * 复用约定：分组集合由设置页、主窗口侧栏、图库页选择菜单三处共享同一实例，
 *          任一处增删分组经 CollectionChanged 自动同步到其余两处；
 *          所有集合修改一律切回 UI 线程执行（仓储内部使用 ConfigureAwait(false)）。
 * 关键约束：分组名称唯一，新增与改名必须校验重名（改名时排除自身），否则会撞 UNIQUE 约束；
 *          删除分组只解除关联、不动条目收藏状态，条目回落到「未分组」；
 *          入组操作隐含置收藏（仓储内同事务完成），调用方无需再单独写收藏。
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>收藏分组管理视图模型。</summary>
public sealed partial class FavoriteGroupViewModel : ObservableObject
{
    private readonly IFavoriteGroupRepository _groups;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    private string _newGroupName = string.Empty;

    /// <summary>正在行内改名的分组主键；null 表示无编辑目标。</summary>
    [ObservableProperty]
    private long? _editingGroupId;

    [ObservableProperty]
    private string _editingGroupName = string.Empty;

    [ObservableProperty]
    private string _groupCountText = "共 0 个分组";

    public FavoriteGroupViewModel(
        IFavoriteGroupRepository groups,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(groups);

        _groups = groups;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>分组集合。</summary>
    public ObservableCollection<FavoriteGroup> Groups { get; } = [];

    /// <summary>是否已有分组；设置页据此在空态与列表间切换。</summary>
    public bool HasGroups => Groups.Count > 0;

    /// <summary>新分组名称是否可用：非空且不与既有分组重名。</summary>
    public bool IsNewGroupNameValid =>
        !string.IsNullOrWhiteSpace(NewGroupName)
        && Groups.All(g => g.Name != NewGroupName.Trim());

    /// <summary>改名草稿是否可用：非空且不与其它分组重名（排除自身）。</summary>
    public bool IsEditingGroupNameValid =>
        !string.IsNullOrWhiteSpace(EditingGroupName)
        && Groups.All(g => g.Id == EditingGroupId || g.Name != EditingGroupName.Trim());

    /// <summary>通知界面校验相关属性需要刷新。</summary>
    public void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(IsNewGroupNameValid));
        OnPropertyChanged(nameof(IsEditingGroupNameValid));
    }

    // 名称草稿变化即刷新重名校验：两个校验属性都依赖草稿与当前集合，
    // 不跟着发通知的话按钮的 CanExecute 会停留在上一次的结果上。
    partial void OnNewGroupNameChanged(string value) => NotifyValidationChanged();

    partial void OnEditingGroupNameChanged(string value) => NotifyValidationChanged();

    /// <summary>加载全部分组。</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var groups = await _groups.GetAllAsync();

        await _dispatcherQueue.EnqueueAsync(() =>
        {
            Groups.Clear();

            foreach (var group in groups)
            {
                Groups.Add(group);
            }

            OnPropertyChanged(nameof(HasGroups));
            GroupCountText = $"共 {Groups.Count} 个分组";
            NotifyValidationChanged();
        });
    }

    /// <summary>清空新增表单。</summary>
    public void ResetNewGroupDraft() => NewGroupName = string.Empty;

    /// <summary>进入行内改名模式。</summary>
    /// <param name="group">目标分组。</param>
    public void BeginEditGroup(FavoriteGroup group)
    {
        EditingGroupId = group.Id;
        EditingGroupName = group.Name;
        NotifyValidationChanged();
    }

    /// <summary>退出行内改名模式。</summary>
    public void CancelEditGroup()
    {
        EditingGroupId = null;
        EditingGroupName = string.Empty;
        NotifyValidationChanged();
    }

    /// <summary>新增分组并重新加载。</summary>
    [RelayCommand(CanExecute = nameof(IsNewGroupNameValid))]
    public async Task AddGroupAsync()
    {
        if (!IsNewGroupNameValid)
        {
            return;
        }

        await _groups.AddAsync(NewGroupName.Trim());
        NewGroupName = string.Empty;
        NotifyValidationChanged();

        await LoadAsync();
    }

    /// <summary>保存行内改名并重新加载。</summary>
    [RelayCommand(CanExecute = nameof(IsEditingGroupNameValid))]
    public async Task RenameGroupAsync()
    {
        if (EditingGroupId is not long id || !IsEditingGroupNameValid)
        {
            return;
        }

        await _groups.RenameAsync(id, EditingGroupName.Trim());
        CancelEditGroup();

        await LoadAsync();
    }

    /// <summary>删除分组；其下关联由数据库级联清除，条目收藏状态不变。</summary>
    /// <param name="group">目标分组。</param>
    [RelayCommand]
    public async Task DeleteGroupAsync(FavoriteGroup? group)
    {
        if (group is null)
        {
            return;
        }

        if (EditingGroupId == group.Id)
        {
            CancelEditGroup();
        }

        await _groups.DeleteAsync(group.Id);
        await LoadAsync();
    }

    /// <summary>批量设定条目的分组归属，供图库页选择菜单调用。</summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="mediaIds">条目主键集合。</param>
    /// <param name="isMember">true 为加入分组，false 为移出分组。</param>
    public async Task ApplyMembershipAsync(
        long groupId,
        IReadOnlyList<long> mediaIds,
        bool isMember)
    {
        if (mediaIds.Count == 0)
        {
            return;
        }

        await _groups.SetMembershipAsync(groupId, mediaIds, isMember);
        await LoadAsync();
    }

    /// <summary>批量取回条目的所属分组，供选择菜单还原勾选态。</summary>
    /// <param name="mediaIds">条目主键集合。</param>
    /// <returns>条目主键到分组主键集合的映射。</returns>
    public Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetGroupIdsByMediaAsync(
        IReadOnlyList<long> mediaIds) =>
        _groups.GetGroupIdsByMediaAsync(mediaIds);
}

/// <summary>收藏下拉菜单中的一个可勾选项；「未分组」以主键 0 表示。</summary>
/// <remarks>属性一律可写（不用 init）：XAML 类型信息生成器会为 x:DataType 引用的类型
///          生成赋值代码，init-only 属性在生成代码里赋值会直接编译失败（CS8852）。</remarks>
public sealed partial class FavoriteGroupOption : ObservableObject
{
    /// <summary>分组主键；0 为「未分组」这一特殊项，不代表真实分组记录。</summary>
    public long GroupId { get; set; }

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否被勾选；与界面双向绑定。</summary>
    [ObservableProperty]
    private bool _isChecked;
}
