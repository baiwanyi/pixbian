/**
 * 图库日期分组视图模型。
 * 职责：承载一个日期分组（组头标题 + 组内条目集合），供分组网格的 GroupStyle 头模板与 ItemsSource 绑定。
 * 复用约定：组内使用 ObservableCollection 以支持分页增量追加时界面自动刷新；
 *          组实例由 GalleryViewModel 在加载时按相邻同日期合并策略创建，外界不得手工增删。
 * 关键约束：条目实例与平铺集合 Items 中的是同一批 MediaItemViewModel，两处集合必须同步修改，
 *          否则收藏状态替换后会出现新旧实例不一致。
 */

using System.Collections.ObjectModel;

namespace Pixbian.ViewModels;

/// <summary>图库的一个日期分组。</summary>
/// <param name="title">组头标题（如「2026年8月29日」）。</param>
public sealed class MediaGroupViewModel(string title)
{
    /// <summary>组头标题。</summary>
    public string Title { get; } = title;

    /// <summary>组内条目集合。</summary>
    public ObservableCollection<MediaItemViewModel> Items { get; } = [];
}
