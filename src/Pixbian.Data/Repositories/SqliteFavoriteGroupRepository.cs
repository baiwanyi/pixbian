/**
 * 收藏分组的 SQLite 仓储实现。
 * 职责：实现分组的增删改名、成员关系的批量写入与清理，以及按条目反查所属分组。
 * 复用约定：全部使用参数化查询，IN 子句的参数名由序号生成、不含外部数据；
 *          每次操作独立连接并依赖连接池，批量写落在单个事务内；
 *          时间以 ISO8601 往返格式文本存取，时间源可注入以便测试。
 * 关键约束：加入分组必须同时把条目置为已收藏（同一事务），否则分组查询会因 is_favorite=0 查不到成员；
 *          移出分组不动收藏状态，收藏语义与分组语义相互独立；
 *          成员计数只统计仍处于收藏态的条目，与界面和查询口径一致；
 *          关联行的清理不依赖数据库级联（外键开关是连接级的），由本仓储与条目仓储各自显式删除。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.Data.Repositories;

/// <summary>收藏分组的 SQLite 仓储。</summary>
public sealed class SqliteFavoriteGroupRepository : IFavoriteGroupRepository
{
    private const int MaxItemsPerBatch = 400;

    private const string SelectColumns = """
        SELECT g.id,
               g.name,
               g.sort_order,
               (SELECT COUNT(*)
                  FROM favorite_group_items AS i
                  INNER JOIN media_items AS m ON m.id = i.media_id
                 WHERE i.group_id = g.id AND m.is_favorite = 1) AS item_count
        FROM favorite_groups AS g
        """;

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化仓储。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public SqliteFavoriteGroupRepository(string connectionString, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FavoriteGroup>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY g.sort_order, g.name;";

        var groups = new List<FavoriteGroup>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(MapGroup(reader));
        }

        return groups;
    }

    /// <inheritdoc />
    public async Task<FavoriteGroup> AddAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindByNameAsync(connection, name, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO favorite_groups (name, sort_order, created_utc)
            VALUES (@name, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM favorite_groups), @created_utc)
            RETURNING id, name, sort_order, 0;
            """;

        command.Parameters.AddWithValue("@name", name.Trim());
        command.Parameters.AddWithValue("@created_utc", FormatUtc(_timeProvider.GetUtcNow()));

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"收藏分组写入失败：{name}");
        }

        return MapGroup(reader);
    }

    /// <inheritdoc />
    public async Task RenameAsync(
        long id,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE favorite_groups SET name = @name WHERE id = @id;";
        command.Parameters.AddWithValue("@name", name.Trim());
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM favorite_groups WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetMembershipAsync(
        long groupId,
        IReadOnlyList<long> mediaIds,
        bool isMember,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);

        if (mediaIds.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 先统一置收藏：分组视图的查询条件带 is_favorite = 1，不置位则成员在分组里查不到。
        if (isMember)
        {
            foreach (var chunk in mediaIds.Chunk(MaxItemsPerBatch))
            {
                var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

                using var favoriteCommand = connection.CreateCommand();
                favoriteCommand.Transaction = transaction;
                favoriteCommand.CommandText = $"UPDATE media_items SET is_favorite = 1 WHERE id IN ({names});";

                for (var i = 0; i < chunk.Length; i++)
                {
                    favoriteCommand.Parameters.AddWithValue($"@p{i}", chunk[i]);
                }

                await favoriteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var addedUtc = FormatUtc(_timeProvider.GetUtcNow());

        foreach (var chunk in mediaIds.Chunk(MaxItemsPerBatch))
        {
            var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            if (isMember)
            {
                // OR IGNORE：重复入组是幂等操作，主键冲突不是错误，无需先查后写。
                command.CommandText =
                    $"INSERT OR IGNORE INTO favorite_group_items (media_id, group_id, added_utc) "
                    + $"SELECT id, @group_id, @added_utc FROM media_items WHERE id IN ({names});";
            }
            else
            {
                command.CommandText =
                    $"DELETE FROM favorite_group_items WHERE group_id = @group_id AND media_id IN ({names});";
            }

            command.Parameters.AddWithValue("@group_id", groupId);
            command.Parameters.AddWithValue("@added_utc", addedUtc);

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearMembershipAsync(
        IReadOnlyList<long> mediaIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);

        if (mediaIds.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var chunk in mediaIds.Chunk(MaxItemsPerBatch))
        {
            var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM favorite_group_items WHERE media_id IN ({names});";

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetGroupIdsByMediaAsync(
        IReadOnlyList<long> mediaIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);

        var accumulator = new Dictionary<long, List<long>>();

        if (mediaIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<long>>();
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in mediaIds.Chunk(MaxItemsPerBatch))
        {
            var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT media_id, group_id FROM favorite_group_items WHERE media_id IN ({names});";

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var mediaId = reader.GetInt64(0);
                var groupId = reader.GetInt64(1);

                if (!accumulator.TryGetValue(mediaId, out var list))
                {
                    list = [];
                    accumulator[mediaId] = list;
                }

                list.Add(groupId);
            }
        }

        // 调用方按 TryGetValue 判空即可得到「无归属」语义，未登记的条目不必补空集合。
        return accumulator.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<long>)pair.Value);
    }

    private static async Task<FavoriteGroup?> FindByNameAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE g.name = @name;";
        command.Parameters.AddWithValue("@name", name.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapGroup(reader)
            : null;
    }

    private static FavoriteGroup MapGroup(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        SortOrder = reader.GetInt32(2),
        ItemCount = reader.GetInt32(3)
    };

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
