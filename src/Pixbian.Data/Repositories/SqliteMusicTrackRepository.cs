/**
 * 音乐曲目的 SQLite 仓储实现。
 * 职责：实现音乐库曲目的读取与全量替换，供短片页随机抽取背景音乐。
 * 复用约定：全部使用参数化查询；时间以 ISO8601 往返格式文本存取；每次操作独立连接并依赖连接池。
 * 关键约束：ReplaceAllAsync 必须在单个事务内完成「先清空后写入」——拆成两次调用会让音乐库
 *           在两次调用之间处于空窗状态，短片页此时抽曲会得到空结果；
 *           批量写入复用预编译语句与参数对象，为每条曲目各建同名参数会互相覆盖导致仅末条生效。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.Data.Repositories;

/// <summary>音乐曲目的 SQLite 仓储。</summary>
public sealed class SqliteMusicTrackRepository : IMusicTrackRepository
{
    private const string SelectColumns = """
        SELECT path, file_name, directory, file_size, added_utc
        FROM music_tracks
        """;

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化仓储。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public SqliteMusicTrackRepository(string connectionString, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MusicTrack>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY path;";

        var tracks = new List<MusicTrack>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(MapTrack(reader));
        }

        return tracks;
    }

    /// <inheritdoc />
    public async Task ReplaceAllAsync(
        IReadOnlyList<MusicTrack> tracks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM music_tracks;";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (tracks.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO music_tracks (path, file_name, directory, file_size, added_utc)
                VALUES (@path, @file_name, @directory, @file_size, @added_utc)
                ON CONFLICT(path) DO UPDATE SET
                    file_name = excluded.file_name,
                    directory = excluded.directory,
                    file_size = excluded.file_size;
                """;

            var pathParam = insert.Parameters.Add("@path", SqliteType.Text);
            var fileNameParam = insert.Parameters.Add("@file_name", SqliteType.Text);
            var directoryParam = insert.Parameters.Add("@directory", SqliteType.Text);
            var fileSizeParam = insert.Parameters.Add("@file_size", SqliteType.Integer);
            var addedUtcParam = insert.Parameters.Add("@added_utc", SqliteType.Text);

            var addedUtc = FormatUtc(_timeProvider.GetUtcNow());

            foreach (var track in tracks)
            {
                pathParam.Value = track.Path;
                fileNameParam.Value = track.FileName;
                directoryParam.Value = track.Directory;
                fileSizeParam.Value = track.FileSize;
                addedUtcParam.Value = addedUtc;

                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MusicTrack MapTrack(SqliteDataReader reader) => new()
    {
        Path = reader.GetString(0),
        FileName = reader.GetString(1),
        Directory = reader.GetString(2),
        FileSize = reader.GetInt64(3),
        AddedUtc = ParseUtc(reader.GetString(4))
    };

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
