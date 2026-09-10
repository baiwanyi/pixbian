/**
 * 索引库快照服务抽象（整库备份与还原）。
 * 职责：把整个索引库导出为一份独立的 SQLite 快照文件，以及用快照替换当前库并保留旧库副本。
 * 复用约定：快照使用 SQLite 的 VACUUM INTO——一致性在线快照，比直接复制库文件安全
 *          （后者可能复制到只写了一半的 WAL 半成品）。
 * 关键约束：还原是**破坏性**操作：会替换当前库并删除其 WAL 附属文件，
 *          且还原后进程内的已加载数据仍是旧的，必须重启应用才能正确；
 *          还原前必须校验快照是有效的 SQLite 库，避免用任意文件覆盖掉用户数据；
 *          还原前必须释放连接池，否则 Windows 上库文件句柄未释放会导致移动失败。
 */

namespace Pixbian.Core.Abstractions;

/// <summary>索引库快照服务。</summary>
public interface IDatabaseSnapshotService
{
    /// <summary>把当前索引库导出为一份独立的快照文件。</summary>
    /// <param name="destinationPath">快照文件路径；已存在时覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际写入的快照文件路径。</returns>
    Task<string> CreateSnapshotAsync(string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>用快照替换当前索引库；旧库保留为带时间戳的副本。</summary>
    /// <param name="snapshotPath">快照文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>旧库备份文件的路径，供界面提示用户。</returns>
    Task<string> RestoreSnapshotAsync(string snapshotPath, CancellationToken cancellationToken = default);

    /// <summary>清理过期的旧库副本（还原产生的 .bak-* 文件）。</summary>
    /// <param name="keepCount">至少保留的最近份数（按文件时间倒序）。</param>
    /// <param name="maxAge">超过该时长的副本即使仍在保留份数内也删除。</param>
    /// <returns>实际删除的文件数。</returns>
    /// <remarks>
    /// 两条规则同时生效：超出最近 <paramref name="keepCount"/> 份的删除；
    /// 即便还在份数内，只要超过 <paramref name="maxAge"/> 也删除——还原是低频操作，
    /// 长期无人过问的副本留着只会占空间。
    /// </remarks>
    int CleanupObsoleteBackups(int keepCount, TimeSpan maxAge);
}
