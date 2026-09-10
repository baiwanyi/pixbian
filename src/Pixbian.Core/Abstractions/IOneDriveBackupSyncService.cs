/**
 * 备份同步服务抽象（OneDrive）。
 * 职责：把用户数据备份写入 OneDrive 目录——固定名 latest 供其它电脑直接导入，
 *      另留一份带机器名与时间戳的历史副本；并提供「是否已到同步周期」的判定。
 * 复用约定：导出复用 IUserDataBackupService，内容与手动导出完全一致；
 *          目标目录来自用户设置，未设置时按环境变量探测 OneDrive 根目录。
 * 关键约束：应用不是常驻进程，「每天 / 每周 / 每月」只能靠启动时检查 + 补齐错过周期实现，
 *          本服务只提供判定与执行，触发时机由外壳负责；
 *          「手动」频率下 IsDue 恒为 false（只能显式调用 SyncAsync）；
 *          备份内容不含任何凭据，同步到云端属用户显式开启的行为，界面必须明示。
 */

using Pixbian.Core.Models;

namespace Pixbian.Core.Abstractions;

/// <summary>备份同步服务。</summary>
public interface IOneDriveBackupSyncService
{
    /// <summary>探测 OneDrive 根目录；未安装或未登录时返回 null。</summary>
    /// <returns>OneDrive 根目录（不含 Pixbian 子目录）；不可用时为 null。</returns>
    string? ResolveOneDriveFolder();

    /// <summary>解析本次同步的目标目录（用户指定优先，否则探测到的 OneDrive 下的 Pixbian 子目录）。</summary>
    /// <param name="settings">当前设置。</param>
    /// <returns>目标目录；不可用时为 null。</returns>
    string? ResolveTargetFolder(AppSettings settings);

    /// <summary>按设置判断当前是否已到同步周期。</summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="now">当前时间（UTC）。</param>
    /// <returns>需要同步时为 true。</returns>
    bool IsDue(AppSettings settings, DateTimeOffset now);

    /// <summary>执行一次同步：导出备份并写入目标目录。</summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果；失败原因写入 <see cref="BackupSyncResult.ErrorMessage"/>。</returns>
    Task<BackupSyncResult> SyncAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>一次备份同步的结果。</summary>
public sealed record BackupSyncResult
{
    /// <summary>是否成功。</summary>
    public bool Succeeded { get; init; }

    /// <summary>本次写入的主备份文件路径；失败时为空串。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>失败原因；成功时为 null。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>完成时间（UTC）。</summary>
    public DateTimeOffset CompletedUtc { get; init; }

    /// <summary>构造失败结果。</summary>
    /// <param name="message">失败原因。</param>
    /// <param name="completedUtc">完成时间。</param>
    public static BackupSyncResult Failure(string message, DateTimeOffset completedUtc) => new()
    {
        Succeeded = false,
        ErrorMessage = message,
        CompletedUtc = completedUtc
    };
}
