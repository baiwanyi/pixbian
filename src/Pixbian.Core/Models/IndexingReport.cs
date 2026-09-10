/**
 * 索引进度与完成报告模型。
 * 职责：承载扫描任务的进度上报与完成报告。
 * 复用约定：进度一律通过 IProgress&lt;T&gt; 上报，禁止在领域服务中直接触碰 UI 线程。
 * 关键约束：报告中的不可访问目录数与对账跳过标记是对账误删防护的可观测出口，
 *          消费方应据此提示用户而非静默处理。
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
