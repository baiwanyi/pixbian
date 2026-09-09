/**
 * 收藏分组模型。
 * 职责：描述用户自建的收藏分组实体，为收藏条目提供「分组」这一第二层归类维度。
 * 复用约定：分组与媒体为多对多关系，关联落 favorite_group_items；
 *          ItemCount 只统计仍处于收藏态的成员，非收藏成员不计入，界面与查询语义保持一致。
 * 关键约束：分组名称唯一，新增与改名必须先在界面层校验重名（编辑时排除自身）；
 *          分组被删除时其下关联行由数据库级联删除，条目回到「未分组」且收藏状态不变；
 *          「未分组」不是一条记录，而是「已收藏且无任何分组关联」的查询语义，禁止为它落库。
 */

namespace Pixbian.Core.Models;

/// <summary>收藏分组。</summary>
public sealed record FavoriteGroup
{
    /// <summary>数据库主键；未入库为 0。</summary>
    public long Id { get; init; }

    /// <summary>分组名称，唯一。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>排序序号，升序排列。</summary>
    public int SortOrder { get; init; }

    /// <summary>仍处于收藏态的成员数量；仅供界面展示，不作为查询条件。</summary>
    public int ItemCount { get; init; }
}
