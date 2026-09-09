/**
 * 索引与目录监控的进度、结果与变更模型（M1）。
 * 职责：承载扫描任务的进度上报、完成报告，以及文件夹监控聚合后的变更批次。
 * 复用约定：进度一律通过 IProgress&lt;T&gt; 上报，禁止在领域服务中直接触碰 UI 线程；
 *          监控事件先经 LibraryWatcherService 去重聚合，再以 LibraryChangeEventArgs 一次性抛出。
 * 关键约束：LibraryChange.Path 已通过 PathGuard 校验，消费方可直接使用；
 *          同一批次内同一路径只保留最后一次变更，避免重复回扫。
 */

namespace Pixbian.Core.Models;

/// <summary>索引进度。</summary>
/// <param name="ProcessedCount">已处理文件数。</param>
public sealed record IndexingProgress(int ProcessedCount);

/// <summary>索引完成报告。</summary>
/// <param name="IndexedCount">本次写入或更新的条目数。</param>
/// <param name="RemovedCount">本次对账移除的失效条目数。</param>
/// <param name="CompletedUtc">完成时间（UTC）。</param>
/// <param name="InaccessibleDirectoryCount">枚举期间无法访问的目录数；大于 0 表示发现集合不完整。</param>
/// <param name="ReconcileSkipped">是否因存在不可访问目录而跳过对账删除。</param>
/// <remarks>
/// 两者均为对账误删防护的可观测出口：不可访问目录会令其子树的条目从发现集合缺席，
/// 此时对账被主动放弃（<see cref="RemovedCount"/> 恒为 0），调用方应据此提示用户而非静默。
/// </remarks>
public sealed record IndexingReport(
    int IndexedCount,
    int RemovedCount,
    DateTimeOffset CompletedUtc,
    int InaccessibleDirectoryCount = 0,
    bool ReconcileSkipped = false);

/// <summary>目录变更类型。</summary>
public enum LibraryChangeKind
{
    /// <summary>新建。</summary>
    Created = 0,

    /// <summary>内容或属性被修改。</summary>
    Modified = 1,

    /// <summary>重命名或移动。</summary>
    Renamed = 2,

    /// <summary>删除。</summary>
    Deleted = 3
}

/// <summary>单条目录变更。</summary>
/// <param name="Kind">变更类型。</param>
/// <param name="Path">当前路径。</param>
/// <param name="OldPath">重命名前的路径；非重命名事件为 null。</param>
public sealed record LibraryChange(LibraryChangeKind Kind, string Path, string? OldPath);

/// <summary>目录变更批次事件参数。</summary>
public sealed class LibraryChangeEventArgs : EventArgs
{
    /// <summary>初始化变更批次。</summary>
    /// <param name="changes">已去重聚合的变更列表。</param>
    public LibraryChangeEventArgs(IReadOnlyList<LibraryChange> changes)
    {
        Changes = changes;
    }

    /// <summary>本批次的变更列表。</summary>
    public IReadOnlyList<LibraryChange> Changes { get; }
}
