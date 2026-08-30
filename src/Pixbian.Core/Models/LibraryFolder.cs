/**
 * 媒体库扫描源模型：代表一个被纳入索引的本地文件夹。
 * 职责：描述扫描源的路径、启用状态与最近扫描时间，供索引服务与监控服务消费。
 * 复用约定：路径统一由 PathGuard 规范化后再写入，确保落库与比对时前缀一致。
 * 关键约束：移除扫描源时，其下所有媒体条目必须一并清理，否则会留下无法访问却又可见的僵尸记录。
 */

namespace Pixbian.Core.Models;

/// <summary>媒体库扫描源。</summary>
public sealed record LibraryFolder
{
    /// <summary>数据库主键；未入库的条目为 0。</summary>
    public long Id { get; init; }

    /// <summary>规范化后的目录完整路径，业务唯一键。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>展示名称；未指定时由目录名派生。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>添加时间（UTC）。</summary>
    public DateTimeOffset AddedUtc { get; init; }

    /// <summary>是否参与扫描与监控；禁用后索引数据保留但不再更新。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>最近一次完成扫描的时间（UTC）。</summary>
    public DateTimeOffset? LastScanUtc { get; init; }
}
