/**
 * 媒体条目的 SQLite 仓储实现。
 * 职责：实现批量 Upsert、目录前缀查询、按路径删除与计数。
 * 复用约定：全部使用参数化查询；时间以 ISO8601 往返格式文本存取；每次操作独立连接并依赖连接池。
 * 关键约束：Upsert 的冲突更新子句必须排除 is_favorite、rating、category_id，否则用户收藏会被扫描覆盖；
 *          目录前缀查询使用 LIKE + ESCAPE，目录名中的 %、_、\ 必须先转义，否则会误命中并导致对账误删；
 *          IN 子句的参数名由索引序号生成（@p0、@p1…），不含任何外部数据，拼接是安全的。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;

namespace Pixbian.Data.Repositories;

/// <summary>媒体条目的 SQLite 仓储。</summary>
public sealed class SqliteMediaItemRepository : IMediaItemRepository
{
    private const int MaxItemsPerBatch = 400;

    private const string UpsertSql = """
        INSERT INTO media_items (
            path, file_name, directory, kind, file_size,
            created_utc, modified_utc, indexed_utc, taken_utc,
            width, height, duration_ms)
        VALUES (
            @path, @file_name, @directory, @kind, @file_size,
            @created_utc, @modified_utc, @indexed_utc, @taken_utc,
            @width, @height, @duration_ms)
        ON CONFLICT(path) DO UPDATE SET
            file_name    = excluded.file_name,
            directory    = excluded.directory,
            kind         = excluded.kind,
            file_size    = excluded.file_size,
            created_utc  = excluded.created_utc,
            modified_utc = excluded.modified_utc,
            indexed_utc  = excluded.indexed_utc,
            taken_utc    = excluded.taken_utc,
            width        = COALESCE(excluded.width, media_items.width),
            height       = COALESCE(excluded.height, media_items.height),
            duration_ms  = COALESCE(excluded.duration_ms, media_items.duration_ms);
        """;

    private const string SelectColumns = """
        SELECT id, path, file_name, directory, kind, file_size,
               created_utc, modified_utc, indexed_utc, taken_utc,
               width, height, duration_ms, is_favorite, category_id, rating
        FROM media_items
        """;

    private readonly string _connectionString;

    /// <summary>初始化仓储。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    public SqliteMediaItemRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task UpsertBatchAsync(
        IReadOnlyList<MediaItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;

        // 语句是单条 INSERT，故逐条执行、复用预编译语句与参数对象；
        // 若一次性为所有条目添加同名参数，后者会覆盖前者，导致仅最后一条生效。
        var pathParam = command.Parameters.Add("@path", SqliteType.Text);
        var fileNameParam = command.Parameters.Add("@file_name", SqliteType.Text);
        var directoryParam = command.Parameters.Add("@directory", SqliteType.Text);
        var kindParam = command.Parameters.Add("@kind", SqliteType.Integer);
        var fileSizeParam = command.Parameters.Add("@file_size", SqliteType.Integer);
        var createdUtcParam = command.Parameters.Add("@created_utc", SqliteType.Text);
        var modifiedUtcParam = command.Parameters.Add("@modified_utc", SqliteType.Text);
        var indexedUtcParam = command.Parameters.Add("@indexed_utc", SqliteType.Text);
        var takenUtcParam = command.Parameters.Add("@taken_utc", SqliteType.Text);
        var widthParam = command.Parameters.Add("@width", SqliteType.Integer);
        var heightParam = command.Parameters.Add("@height", SqliteType.Integer);
        var durationMsParam = command.Parameters.Add("@duration_ms", SqliteType.Integer);

        foreach (var item in items)
        {
            pathParam.Value = item.Path;
            fileNameParam.Value = item.FileName;
            directoryParam.Value = item.Directory;
            kindParam.Value = (int)item.Kind;
            fileSizeParam.Value = item.FileSize;
            createdUtcParam.Value = FormatUtc(item.CreatedUtc);
            modifiedUtcParam.Value = FormatUtc(item.ModifiedUtc);
            indexedUtcParam.Value = FormatUtc(item.IndexedUtc);
            takenUtcParam.Value = FormatNullableUtc(item.TakenUtc);
            widthParam.Value = (object?)item.Width ?? DBNull.Value;
            heightParam.Value = (object?)item.Height ?? DBNull.Value;
            durationMsParam.Value = (object?)item.DurationMs ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetPathsUnderDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = EscapeLikePattern(normalized + Path.DirectorySeparatorChar) + "%";

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            WHERE directory = @directory OR directory LIKE @prefix ESCAPE '\'
            """;

        command.Parameters.AddWithValue("@directory", normalized);
        command.Parameters.AddWithValue("@prefix", prefix);

        var paths = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(1));
        }

        return paths;
    }

    /// <inheritdoc />
    public async Task DeleteByPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var chunk in paths.Chunk(MaxItemsPerBatch))
        {
            // 参数名由序号生成，不含外部数据；实际值全部通过参数绑定传入。
            var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM media_items WHERE path IN ({names});";

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaItem>> QueryAsync(
        MediaQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var searchPattern = string.IsNullOrWhiteSpace(query.SearchText)
            ? null
            : $"%{EscapeLikePattern(query.SearchText.Trim())}%";

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // 排序键与方向都通过参数化的 CASE 表达式切换，避免把排序字段拼进 SQL 字符串。
        // 随机排序用主键乘以种子再取模：同一种子下顺序稳定，增量分页才不会重复或漏条目。
        command.CommandText = $$"""
            {{SelectColumns}}
            WHERE deleted_utc IS NULL
              AND (@kind IS NULL OR kind = @kind)
              AND (@category IS NULL OR category_id = @category)
              AND (@favorite IS NULL OR is_favorite = @favorite)
              AND (@search IS NULL OR file_name LIKE @search ESCAPE '\')
            ORDER BY
              CASE WHEN @sortKey = 0 THEN (id * @seed) % 1000003 END,
              CASE WHEN @sortKey = 1 AND @direction = 0 THEN modified_utc END ASC,
              CASE WHEN @sortKey = 1 AND @direction = 1 THEN modified_utc END DESC,
              CASE WHEN @sortKey = 2 AND @direction = 0 THEN file_size END ASC,
              CASE WHEN @sortKey = 2 AND @direction = 1 THEN file_size END DESC,
              CASE WHEN @sortKey = 3 AND @direction = 0 THEN file_name END ASC,
              CASE WHEN @sortKey = 3 AND @direction = 1 THEN file_name END DESC,
              file_name ASC
            LIMIT @take OFFSET @skip;
            """;

        command.Parameters.AddWithValue("@kind", query.Kind.HasValue ? (object)(int)query.Kind.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "@category",
            query.CategoryId.HasValue ? query.CategoryId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "@favorite",
            query.IsFavorite.HasValue ? query.IsFavorite.Value : DBNull.Value);
        command.Parameters.AddWithValue("@search", (object?)searchPattern ?? DBNull.Value);
        command.Parameters.AddWithValue("@sortKey", (int)query.SortKey);
        command.Parameters.AddWithValue("@direction", (int)query.SortDirection);
        command.Parameters.AddWithValue("@seed", query.RandomSeed);
        command.Parameters.AddWithValue("@take", query.Take);
        command.Parameters.AddWithValue("@skip", query.Skip);

        var items = new List<MediaItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(MapItem(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<MediaItem?> GetAtOffsetAsync(
        MediaKind? kind,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "偏移量不能为负数。");
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // 按主键定位，避免在 SQL 层使用 ORDER BY RANDOM() —— 后者会对全表排序，
        // 数据量上万后开销无法接受。随机性由应用层生成的偏移量提供。
        command.CommandText = $$"""
            {{SelectColumns}}
            WHERE deleted_utc IS NULL
              AND (@kind IS NULL OR kind = @kind)
            ORDER BY id
            LIMIT 1 OFFSET @offset;
            """;

        command.Parameters.AddWithValue("@kind", kind.HasValue ? (object)(int)kind.Value : DBNull.Value);
        command.Parameters.AddWithValue("@offset", offset);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapItem(reader) : null;
    }

    /// <inheritdoc />
    public async Task<MediaItem?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapItem(reader) : null;
    }

    /// <inheritdoc />
    public async Task SetFavoriteAsync(
        IReadOnlyList<long> ids,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var chunk in ids.Chunk(MaxItemsPerBatch))
        {
            // 参数名由序号生成，不含外部数据；实际值全部通过参数绑定传入。
            var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE media_items SET is_favorite = @value WHERE id IN ({names});";
            command.Parameters.AddWithValue("@value", isFavorite ? 1 : 0);

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var chunk in ids.Chunk(MaxItemsPerBatch))
        {
            // 参数名由序号生成，不含外部数据；实际值全部通过参数绑定传入。
            var names = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM media_items WHERE id IN ({names});";

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MediaItem MapItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Path = reader.GetString(1),
        FileName = reader.GetString(2),
        Directory = reader.GetString(3),
        Kind = (MediaKind)reader.GetInt32(4),
        FileSize = reader.GetInt64(5),
        CreatedUtc = ParseUtc(reader.GetString(6)),
        ModifiedUtc = ParseUtc(reader.GetString(7)),
        IndexedUtc = ParseUtc(reader.GetString(8)),
        TakenUtc = reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
        Width = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        Height = reader.IsDBNull(11) ? null : reader.GetInt32(11),
        DurationMs = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        IsFavorite = reader.GetInt32(13) != 0,
        CategoryId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        Rating = reader.GetInt32(15)
    };

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <inheritdoc />
    public async Task<int> CountAsync(
        MediaKind? kind,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns.Replace("SELECT id,", "SELECT COUNT(*)", StringComparison.Ordinal)}"
            + " WHERE deleted_utc IS NULL AND (@kind IS NULL OR kind = @kind);";

        command.Parameters.AddWithValue("@kind", kind.HasValue ? (object)(int)kind.Value : DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableUtc(DateTimeOffset? value) =>
        value.HasValue ? FormatUtc(value.Value) : DBNull.Value;

    /// <summary>转义 LIKE 通配符，避免目录名中的 %、_、\ 被当作模式字符。</summary>
    private static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
