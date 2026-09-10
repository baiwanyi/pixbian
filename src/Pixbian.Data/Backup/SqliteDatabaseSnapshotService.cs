/**
 * 索引库快照服务的 SQLite 实现。
 * 职责：用 VACUUM INTO 导出整库一致性快照；用快照替换当前库（旧库改名保留）。
 * 复用约定：连接字符串由 SqliteConnectionStringBuilder 构建；快照写入走「临时文件 + 原子替换」；
 *          还原前的有效性校验走 PRAGMA integrity_check。
 * 关键约束：还原前必须 SqliteConnection.ClearAllPools 释放句柄，否则 Windows 上文件被占用无法移动；
 *          还原必须删除旧库的 -wal / -shm：它们是旧库的事务日志，留着会把旧数据带回新库；
 *          还原是破坏性操作，调用方必须先完成二次确认；本服务只保证「文件层面」正确，
 *          进程内的内存状态需由界面提示用户重启应用来收敛。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;

namespace Pixbian.Data.Backup;

/// <summary>索引库快照服务。</summary>
public sealed class SqliteDatabaseSnapshotService : IDatabaseSnapshotService
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化快照服务。</summary>
    /// <param name="databasePath">索引库文件路径（还原时需要直接操作文件）。</param>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public SqliteDatabaseSnapshotService(
        string databasePath,
        string connectionString,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _databasePath = databasePath;
        _connectionString = connectionString;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<string> CreateSnapshotAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("快照目标必须是绝对路径。", nameof(destinationPath));
        }

        EnsureParentDirectory(destinationPath);

        // VACUUM INTO 要求目标文件不存在，故先写临时文件再原子替换（已存在的旧快照随之被覆盖）。
        var temporaryPath = destinationPath + ".tmp";
        TryDelete(temporaryPath);

        try
        {
            await using (var connection = new SqliteConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = connection.CreateCommand();

                // 目标路径走参数绑定：路径来自文件选择器但仍属外部输入，不做字符串拼接。
                command.CommandText = "VACUUM INTO @target;";
                command.Parameters.AddWithValue("@target", temporaryPath);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> RestoreSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        await ValidateSnapshotAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        // 释放连接池：库文件仍被池内连接持有句柄时，Windows 上的移动/删除会直接失败。
        SqliteConnection.ClearAllPools();

        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupPath = $"{_databasePath}.bak-{stamp}";

        if (File.Exists(_databasePath))
        {
            File.Move(_databasePath, backupPath, overwrite: true);
        }

        // 旧库的 WAL 与共享内存必须一并清理：它们是旧库的事务日志，
        // 留着会让新库读到旧库未提交的数据，属于最隐蔽的一类数据错乱。
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");

        File.Copy(snapshotPath, _databasePath, overwrite: true);

        return backupPath;
    }

    /// <summary>校验快照是结构完整的 SQLite 库；只读打开且不启用连接池，避免占用文件句柄。</summary>
    private static async Task ValidateSnapshotAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(snapshotPath))
        {
            throw new FileNotFoundException($"快照文件不存在：{snapshotPath}", snapshotPath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,

            // 校验连接不参与池化：池会延长文件句柄的存活期，随后复制该文件可能失败。
            Pooling = false
        };

        try
        {
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (result is not string text || !string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("所选文件不是有效的索引库快照（完整性校验未通过）。");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"所选文件不是有效的索引库快照：{ex.Message}", ex);
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败只会留下一个附属文件，不改变主流程结果。
        }
    }
}
