/**
 * 媒体库扫描源的 SQLite 仓储实现。
 * 职责：实现扫描源的增删改查与扫描时间更新。
 * 复用约定：路径统一规范化后入库，保证唯一键与前缀比对结果一致；全部查询参数化。
 * 关键约束：AddAsync 对已存在路径返回既有记录而非抛异常，避免重复添加导致索引任务重复执行；
 *          移除扫描源时不级联删除其下媒体条目，该职责留给上层调用方在事务内显式处理。
 */

using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;

namespace Pixbian.Data.Repositories;

/// <summary>媒体库扫描源的 SQLite 仓储。</summary>
public sealed class SqliteLibraryFolderRepository : ILibraryFolderRepository
{
    private const string SelectColumns = """
        SELECT id, path, display_name, added_utc, is_enabled, last_scan_utc
        FROM library_folders
        """;

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化仓储。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public SqliteLibraryFolderRepository(string connectionString, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryFolder>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY path;";

        var folders = new List<LibraryFolder>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(MapFolder(reader));
        }

        return folders;
    }

    /// <inheritdoc />
    public async Task<LibraryFolder> AddAsync(
        string path,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = PathGuard.NormalizeDirectory(path);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar))
            : displayName;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindByPathAsync(connection, normalized, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_folders (path, display_name, added_utc, is_enabled)
            VALUES (@path, @display_name, @added_utc, 1)
            RETURNING id, path, display_name, added_utc, is_enabled, last_scan_utc;
            """;

        command.Parameters.AddWithValue("@path", normalized);
        command.Parameters.AddWithValue("@display_name", name);
        command.Parameters.AddWithValue("@added_utc", FormatUtc(_timeProvider.GetUtcNow()));

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"扫描源写入失败：{normalized}");
        }

        return MapFolder(reader);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_folders WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(
        long id,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_folders SET is_enabled = @is_enabled WHERE id = @id;";
        command.Parameters.AddWithValue("@is_enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateLastScanAsync(
        long id,
        DateTimeOffset scannedUtc,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_folders SET last_scan_utc = @last_scan_utc WHERE id = @id;";
        command.Parameters.AddWithValue("@last_scan_utc", FormatUtc(scannedUtc));
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按规范化路径查询既有扫描源。</summary>
    private static async Task<LibraryFolder?> FindByPathAsync(
        SqliteConnection connection,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE path = @path;";
        command.Parameters.AddWithValue("@path", normalizedPath);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapFolder(reader)
            : null;
    }

    private static LibraryFolder MapFolder(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Path = reader.GetString(1),
        DisplayName = reader.GetString(2),
        AddedUtc = ParseUtc(reader.GetString(3)),
        IsEnabled = reader.GetInt32(4) != 0,
        LastScanUtc = reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5))
    };

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
