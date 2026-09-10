/**
 * 用户数据备份服务的 SQLite 实现。
 * 职责：把索引库中的用户数据（收藏与评分、收藏分组、分类与规则、条目分类归属、扫描源）
 *      导出为单个 JSON 数据包，并按「合并、导入文件为准」的策略导入回索引库。
 * 复用约定：条目以 media_items.path 匹配（COLLATE NOCASE，兼容盘符大小写差异）、
 *          分类与分组按名称合并；导出写入沿用「临时文件 + 原子替换」；
 *          动态 IN 子句的参数名由序号生成，全部走参数绑定。
 * 关键约束：导入整体落在单事务内，任一步失败即回滚，杜绝「导入一半」的脏状态；
 *          规则正则导入前必须过 CategoryRuleHelper.ValidatePattern——损坏或恶意备份
 *          不得把灾难性回溯的正则灌进库（ReDoS）；
 *          收藏取并集（只置位、不清除本地收藏），评分只升不降：导入是「补齐 + 更新」
 *          而非快照还原，覆盖式回写会丢用户数据；
 *          备份文件不含任何凭据（Web 密码哈希绑定本机用户，跨机导入必然失效）。
 */

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Data.Sqlite;

namespace Pixbian.Data.Backup;

/// <summary>用户数据备份的 SQLite 实现。</summary>
public sealed class SqliteUserDataBackupService : IUserDataBackupService
{
    /// <summary>单条语句的参数上限（SQLite 默认变量数上限为 999，此处留足余量）。</summary>
    private const int MaxParametersPerStatement = 400;

    /// <summary>备份文件大小上限：正常数据包在数 MB 以内，超限只可能是损坏或恶意文件。</summary>
    private const long MaxBackupFileBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化备份服务。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="timeProvider">时间提供器；为空时使用系统时间。</param>
    public SqliteUserDataBackupService(string connectionString, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<UserDataBackupCounts> ExportAsync(
        string destinationPath,
        IReadOnlyList<string> musicFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(musicFolders);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var favorites = await ReadFavoritesAsync(connection, cancellationToken).ConfigureAwait(false);
        var categories = await ReadCategoriesAsync(connection, cancellationToken).ConfigureAwait(false);
        var rules = await ReadRulesAsync(connection, cancellationToken).ConfigureAwait(false);
        var groups = await ReadGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        var itemCategories = await ReadItemCategoriesAsync(connection, cancellationToken).ConfigureAwait(false);
        var folders = await ReadLibraryFoldersAsync(connection, cancellationToken).ConfigureAwait(false);

        var counts = new UserDataBackupCounts
        {
            Favorites = favorites.Count,
            Groups = groups.Count,
            Categories = categories.Count,
            Rules = rules.Count,
            Folders = folders.Count
        };

        var document = new UserDataBackupDocument
        {
            AppVersion = ResolveAppVersion(),
            SchemaVersion = SchemaMigrations.CurrentVersion,
            ExportedUtc = _timeProvider.GetUtcNow(),
            Counts = counts,
            Favorites = favorites,
            Groups = groups,
            Categories = categories,
            Rules = rules,
            ItemCategories = itemCategories,
            LibraryFolders = folders,
            MusicFolders = musicFolders
        };

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var temporaryPath = destinationPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, Utf8WithoutBom, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            // 失败或取消时不留半截临时文件；目标文件本身尚未被触碰。
            TryDelete(temporaryPath);
            throw;
        }

        return counts;
    }

    /// <inheritdoc />
    public async Task<UserDataImportResult> ImportAsync(
        string sourcePath,
        UserDataImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);

        var document = await ReadAndValidateAsync(sourcePath, cancellationToken).ConfigureAwait(false);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // 路径 → 条目主键：一次性解析，供收藏、分组成员与分类归属共用。
        var allPaths = CollectPaths(document, options.ImportItemCategories);
        var mediaIds = await ResolveMediaIdsAsync(
                connection,
                transaction,
                allPaths,
                options.PathMappings,
                cancellationToken)
            .ConfigureAwait(false);

        var nowUtc = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        var (categoryIds, added, updated) = await MergeCategoriesAsync(
                connection, transaction, document.Categories, cancellationToken)
            .ConfigureAwait(false);

        var skippedRules = 0;
        var (ruleAdded, ruleUpdated, skipped) = await MergeRulesAsync(
                connection, transaction, document.Rules, categoryIds, cancellationToken)
            .ConfigureAwait(false);
        added += ruleAdded;
        updated += ruleUpdated;
        skippedRules += skipped;

        var (groupAdded, groupUpdated) = await MergeGroupsAsync(
                connection, transaction, document.Groups, mediaIds, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        added += groupAdded;
        updated += groupUpdated;

        var favoriteCount = await MergeFavoritesAsync(
                connection, transaction, document.Favorites, mediaIds, cancellationToken)
            .ConfigureAwait(false);

        if (options.ImportItemCategories)
        {
            await MergeItemCategoriesAsync(
                    connection, transaction, document.ItemCategories, categoryIds, mediaIds, cancellationToken)
                .ConfigureAwait(false);
        }

        await MergeLibraryFoldersAsync(connection, transaction, document.LibraryFolders, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new UserDataImportResult
        {
            Added = added,
            Updated = updated,
            Favorites = favoriteCount,
            Unmatched = mediaIds.UnmatchedPaths.Count,
            SkippedRules = skippedRules,
            UnmatchedPaths = mediaIds.UnmatchedPaths,
            MusicFolders = document.MusicFolders
        };
    }

    /// <summary>读取备份文件并校验格式头与版本。</summary>
    private static async Task<UserDataBackupDocument> ReadAndValidateAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(sourcePath);

        if (!info.Exists)
        {
            throw new FileNotFoundException($"备份文件不存在：{sourcePath}", sourcePath);
        }

        if (info.Length > MaxBackupFileBytes)
        {
            throw new InvalidDataException($"备份文件过大（上限 {MaxBackupFileBytes / (1024 * 1024)} MB），已拒绝读取。");
        }

        var json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        UserDataBackupDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<UserDataBackupDocument>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"备份文件不是有效的 JSON：{ex.Message}", ex);
        }

        if (document is null)
        {
            throw new InvalidDataException("备份文件内容为空。");
        }

        if (!string.Equals(document.Format, UserDataBackupFormat.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"备份文件格式标识不匹配（期望 {UserDataBackupFormat.Name}）。");
        }

        if (document.FormatVersion > UserDataBackupFormat.CurrentVersion)
        {
            throw new InvalidDataException(
                $"备份文件版本（{document.FormatVersion}）高于当前应用支持的版本"
                + $"（{UserDataBackupFormat.CurrentVersion}），请先升级应用。");
        }

        return document;
    }

    /// <summary>收集文档中出现的全部文件路径（去重，大小写不敏感）。</summary>
    private static List<string> CollectPaths(UserDataBackupDocument document, bool includeItemCategories)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var favorite in document.Favorites)
        {
            AddPath(paths, favorite.Path);
        }

        foreach (var group in document.Groups)
        {
            foreach (var item in group.Items)
            {
                AddPath(paths, item);
            }
        }

        if (includeItemCategories)
        {
            foreach (var item in document.ItemCategories)
            {
                AddPath(paths, item.Path);
            }
        }

        return paths.ToList();
    }

    private static void AddPath(HashSet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }

    /// <summary>把备份中的路径批量映射到当前库中的条目主键。</summary>
    /// <returns>映射结果（键为备份中的原始路径）与未匹配路径清单。</returns>
    /// <remarks>
    /// 匹配走 COLLATE NOCASE：盘符与目录名的大小写在不同机器上可能不一致，
    /// 二进制比较会把同一文件判成两个。备份中不存在于库里的路径计入未匹配，
    /// 它们多半是文件已被移动或删除（导入时一并报给用户定位）。
    /// </remarks>
    private static async Task<ResolvedMediaIds> ResolveMediaIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> paths,
        IReadOnlyList<PathPrefixMapping> mappings,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();

        if (paths.Count == 0)
        {
            return new ResolvedMediaIds(resolved, unmatched);
        }

        var lookup = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in paths
                     .Select(path => ApplyMappings(path, mappings))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Chunk(MaxParametersPerStatement))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT id, path FROM media_items WHERE path COLLATE NOCASE IN ({BuildParameterList(chunk.Length)});";

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lookup[reader.GetString(1)] = reader.GetInt64(0);
            }
        }

        foreach (var path in paths)
        {
            if (lookup.TryGetValue(ApplyMappings(path, mappings), out var id))
            {
                resolved[path] = id;
            }
            else
            {
                unmatched.Add(path);
            }
        }

        return new ResolvedMediaIds(resolved, unmatched);
    }

    /// <summary>按前缀映射改写路径；无匹配映射时原样返回。</summary>
    /// <remarks>
    /// 前缀必须落在目录边界上：<c>D:\Lib</c> 不得命中 <c>D:\Library\a.jpg</c>——
    /// 目录名互为前缀是常见情形（Lib / Library、Photos / Photos_2025），
    /// 只做 StartsWith 会把用户没打算迁移的文件也一起改掉。
    /// 前缀自身以分隔符结尾时视为已带边界，无需再判。
    /// </remarks>
    private static string ApplyMappings(string path, IReadOnlyList<PathPrefixMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.From) || string.IsNullOrWhiteSpace(mapping.To)
                || !path.StartsWith(mapping.From, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fromEndsWithSeparator = mapping.From.EndsWith(Path.DirectorySeparatorChar)
                || mapping.From.EndsWith(Path.AltDirectorySeparatorChar);
            var boundary = path.Length == mapping.From.Length ? '\0' : path[mapping.From.Length];

            if (!fromEndsWithSeparator && boundary is not ('\\' or '/') && boundary != '\0')
            {
                continue;
            }

            var prefix = mapping.To.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Concat(prefix, path.AsSpan(mapping.From.Length));
        }

        return path;
    }

    /// <summary>合并分类：按名称匹配（存在则更新、不存在则新建），返回名称 → 主键映射与增改计数。</summary>
    /// <remarks>名称 → 主键映射同时供规则与条目归属使用，故合并在同一趟内完成。</remarks>
    private static async Task<(Dictionary<string, long> Ids, int Added, int Updated)> MergeCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BackupCategory> categories,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var updated = 0;

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.Name))
            {
                continue;
            }

            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM categories WHERE name = @name;";
            select.Parameters.AddWithValue("@name", category.Name);
            var existing = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (existing is not null and not DBNull)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE categories SET color = @color, sort_order = @sort_order, is_enabled = @is_enabled "
                    + "WHERE id = @id;";
                update.Parameters.AddWithValue("@color", (object?)category.Color ?? DBNull.Value);
                update.Parameters.AddWithValue("@sort_order", category.SortOrder);
                update.Parameters.AddWithValue("@is_enabled", category.IsEnabled ? 1 : 0);
                update.Parameters.AddWithValue("@id", Convert.ToInt64(existing, CultureInfo.InvariantCulture));

                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                map[category.Name] = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                updated++;
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO categories (name, color, sort_order, is_enabled) "
                + "VALUES (@name, @color, @sort_order, @is_enabled) RETURNING id;";
            insert.Parameters.AddWithValue("@name", category.Name);
            insert.Parameters.AddWithValue("@color", (object?)category.Color ?? DBNull.Value);
            insert.Parameters.AddWithValue("@sort_order", category.SortOrder);
            insert.Parameters.AddWithValue("@is_enabled", category.IsEnabled ? 1 : 0);

            var inserted = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            map[category.Name] = Convert.ToInt64(inserted, CultureInfo.InvariantCulture);
            added++;
        }

        return (map, added, updated);
    }

    /// <summary>合并分类规则：按 (名称, 正则, 目标) 业务键去重，正则不安全的整条跳过。</summary>
    private static async Task<(int Added, int Updated, int Skipped)> MergeRulesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BackupRule> rules,
        Dictionary<string, long> categoryIds,
        CancellationToken cancellationToken)
    {
        var existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, name, pattern, target FROM category_rules;";

            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing[BuildRuleKey(reader.GetString(1), reader.GetString(2), reader.GetInt32(3))]
                    = reader.GetInt64(0);
            }
        }

        var added = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name)
                || !categoryIds.TryGetValue(rule.Category ?? string.Empty, out var categoryId))
            {
                skipped++;
                continue;
            }

            // 安全闸门：正则来自外部文件，必须校验（超长、语法错误、灾难性回溯一律拒绝）。
            if (!CategoryRuleHelper.ValidatePattern(rule.Pattern).IsValid)
            {
                skipped++;
                continue;
            }

            var key = BuildRuleKey(rule.Name, rule.Pattern, (int)rule.Target);

            if (existing.TryGetValue(key, out var id))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE category_rules SET category_id = @category_id, is_enabled = @is_enabled, "
                    + "priority = @priority, is_case_sensitive = @is_case_sensitive WHERE id = @id;";
                update.Parameters.AddWithValue("@category_id", categoryId);
                update.Parameters.AddWithValue("@is_enabled", rule.IsEnabled ? 1 : 0);
                update.Parameters.AddWithValue("@priority", rule.Priority);
                update.Parameters.AddWithValue("@is_case_sensitive", rule.IsCaseSensitive ? 1 : 0);
                update.Parameters.AddWithValue("@id", id);

                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                updated++;
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO category_rules "
                + "(name, pattern, category_id, target, is_enabled, priority, is_case_sensitive) "
                + "VALUES (@name, @pattern, @category_id, @target, @is_enabled, @priority, @is_case_sensitive);";
            insert.Parameters.AddWithValue("@name", rule.Name);
            insert.Parameters.AddWithValue("@pattern", rule.Pattern);
            insert.Parameters.AddWithValue("@category_id", categoryId);
            insert.Parameters.AddWithValue("@target", (int)rule.Target);
            insert.Parameters.AddWithValue("@is_enabled", rule.IsEnabled ? 1 : 0);
            insert.Parameters.AddWithValue("@priority", rule.Priority);
            insert.Parameters.AddWithValue("@is_case_sensitive", rule.IsCaseSensitive ? 1 : 0);

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            added++;
        }

        return (added, updated, skipped);
    }

    /// <summary>合并收藏分组及其成员；成员一律同时置为已收藏（与分组查询口径一致）。</summary>
    private static async Task<(int Added, int Updated)> MergeGroupsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BackupGroup> groups,
        ResolvedMediaIds mediaIds,
        string nowUtc,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }

            long groupId;

            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id FROM favorite_groups WHERE name = @name;";
                select.Parameters.AddWithValue("@name", group.Name);
                var existing = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                if (existing is not null and not DBNull)
                {
                    groupId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);

                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE favorite_groups SET sort_order = @sort_order WHERE id = @id;";
                    update.Parameters.AddWithValue("@sort_order", group.SortOrder);
                    update.Parameters.AddWithValue("@id", groupId);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    updated++;
                }
                else
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO favorite_groups (name, sort_order, created_utc) "
                        + "VALUES (@name, @sort_order, @created_utc) RETURNING id;";
                    insert.Parameters.AddWithValue("@name", group.Name);
                    insert.Parameters.AddWithValue("@sort_order", group.SortOrder);
                    insert.Parameters.AddWithValue(
                        "@created_utc",
                        group.CreatedUtc is { } created
                            ? created.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                            : nowUtc);

                    var inserted = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    groupId = Convert.ToInt64(inserted, CultureInfo.InvariantCulture);
                    added++;
                }
            }

            var memberIds = group.Items
                .Select(path => mediaIds.Map.TryGetValue(path, out var id) ? id : (long?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            if (memberIds.Count == 0)
            {
                continue;
            }

            foreach (var chunk in memberIds.Chunk(MaxParametersPerStatement))
            {
                var parameters = BuildParameterList(chunk.Length);

                using var favorite = connection.CreateCommand();
                favorite.Transaction = transaction;
                favorite.CommandText = $"UPDATE media_items SET is_favorite = 1 WHERE id IN ({parameters});";

                for (var i = 0; i < chunk.Length; i++)
                {
                    favorite.Parameters.AddWithValue($"@p{i}", chunk[i]);
                }

                await favorite.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                using var member = connection.CreateCommand();
                member.Transaction = transaction;
                member.CommandText =
                    $"INSERT OR IGNORE INTO favorite_group_items (media_id, group_id, added_utc) "
                    + $"SELECT id, @group_id, @added_utc FROM media_items WHERE id IN ({parameters});";
                member.Parameters.AddWithValue("@group_id", groupId);
                member.Parameters.AddWithValue("@added_utc", nowUtc);

                for (var i = 0; i < chunk.Length; i++)
                {
                    member.Parameters.AddWithValue($"@p{i}", chunk[i]);
                }

                await member.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return (added, updated);
    }

    /// <summary>合并收藏与评分：收藏取并集（只置位），评分只升不降。</summary>
    private static async Task<int> MergeFavoritesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BackupFavorite> favorites,
        ResolvedMediaIds mediaIds,
        CancellationToken cancellationToken)
    {
        var matched = favorites
            .Where(favorite => mediaIds.Map.TryGetValue(favorite.Path, out _))
            .ToList();

        if (matched.Count == 0)
        {
            return 0;
        }

        var ids = matched
            .Select(favorite => mediaIds.Map[favorite.Path])
            .Distinct()
            .ToList();

        foreach (var chunk in ids.Chunk(MaxParametersPerStatement))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"UPDATE media_items SET is_favorite = 1 WHERE id IN ({BuildParameterList(chunk.Length)});";

            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", chunk[i]);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 评分逐条更新：MAX(rating, @rating) 保证只升不降——备份里的评分可能低于本机
        // （例如备份较早），覆盖会丢用户此后的标注。
        foreach (var favorite in matched.Where(favorite => favorite.Rating > 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE media_items SET rating = MAX(rating, @rating) WHERE id = @id;";
            command.Parameters.AddWithValue("@rating", favorite.Rating);
            command.Parameters.AddWithValue("@id", mediaIds.Map[favorite.Path]);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return matched.Count;
    }

    /// <summary>写入条目的分类归属（可选步骤；规则「重新匹配」会覆盖它）。</summary>
    private static async Task MergeItemCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BackupItemCategory> itemCategories,
        Dictionary<string, long> categoryIds,
        ResolvedMediaIds mediaIds,
        CancellationToken cancellationToken)
    {
        foreach (var entry in itemCategories)
        {
            if (!categoryIds.TryGetValue(entry.Category ?? string.Empty, out var categoryId)
                || !mediaIds.Map.TryGetValue(entry.Path, out var mediaId))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE media_items SET category_id = @category_id WHERE id = @id;";
            command.Parameters.AddWithValue("@category_id", categoryId);
            command.Parameters.AddWithValue("@id", mediaId);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>合并扫描源：路径已存在时跳过（INSERT OR IGNORE），不覆盖本机的启用状态。</summary>
    private static async Task MergeLibraryFoldersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<BackupLibraryFolder> folders,
        string nowUtc,
        CancellationToken cancellationToken)
    {

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(folder.DisplayName)
                ? Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : folder.DisplayName;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR IGNORE INTO library_folders (path, display_name, added_utc, is_enabled, last_scan_utc) "
                + "VALUES (@path, @display_name, @added_utc, @is_enabled, NULL);";
            command.Parameters.AddWithValue("@path", folder.Path);
            command.Parameters.AddWithValue("@display_name", displayName);
            command.Parameters.AddWithValue("@added_utc", nowUtc);
            command.Parameters.AddWithValue("@is_enabled", folder.IsEnabled ? 1 : 0);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<BackupFavorite>> ReadFavoritesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, rating FROM media_items WHERE is_favorite = 1 ORDER BY path;";

        var favorites = new List<BackupFavorite>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            favorites.Add(new BackupFavorite
            {
                Path = reader.GetString(0),
                Rating = reader.GetInt32(1)
            });
        }

        return favorites;
    }

    private static async Task<IReadOnlyList<BackupCategory>> ReadCategoriesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, color, sort_order, is_enabled FROM categories ORDER BY sort_order, name;";

        var categories = new List<BackupCategory>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(new BackupCategory
            {
                Name = reader.GetString(0),
                Color = reader.IsDBNull(1) ? null : reader.GetString(1),
                SortOrder = reader.GetInt32(2),
                IsEnabled = reader.GetInt32(3) != 0
            });
        }

        return categories;
    }

    private static async Task<IReadOnlyList<BackupRule>> ReadRulesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.name, r.pattern, c.name, r.target, r.is_enabled, r.priority, r.is_case_sensitive
              FROM category_rules AS r
              INNER JOIN categories AS c ON c.id = r.category_id
             ORDER BY r.priority DESC, r.name;
            """;

        var rules = new List<BackupRule>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new BackupRule
            {
                Name = reader.GetString(0),
                Pattern = reader.GetString(1),
                Category = reader.GetString(2),
                Target = (RuleMatchTarget)reader.GetInt32(3),
                IsEnabled = reader.GetInt32(4) != 0,
                Priority = reader.GetInt32(5),
                IsCaseSensitive = reader.GetInt32(6) != 0
            });
        }

        return rules;
    }

    private static async Task<IReadOnlyList<BackupGroup>> ReadGroupsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var members = new Dictionary<long, List<string>>();

        using (var memberCommand = connection.CreateCommand())
        {
            memberCommand.CommandText = """
                SELECT i.group_id, m.path
                  FROM favorite_group_items AS i
                  INNER JOIN media_items AS m ON m.id = i.media_id
                 ORDER BY i.group_id, m.path;
                """;

            using var memberReader = await memberCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await memberReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var groupId = memberReader.GetInt64(0);

                if (!members.TryGetValue(groupId, out var list))
                {
                    list = [];
                    members[groupId] = list;
                }

                list.Add(memberReader.GetString(1));
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, sort_order, created_utc FROM favorite_groups ORDER BY sort_order, name;";

        var groups = new List<BackupGroup>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);

            groups.Add(new BackupGroup
            {
                Name = reader.GetString(1),
                SortOrder = reader.GetInt32(2),
                CreatedUtc = reader.IsDBNull(3)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Items = members.TryGetValue(id, out var list) ? list : []
            });
        }

        return groups;
    }

    private static async Task<IReadOnlyList<BackupItemCategory>> ReadItemCategoriesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.path, c.name
              FROM media_items AS m
              INNER JOIN categories AS c ON c.id = m.category_id
             ORDER BY m.path;
            """;

        var items = new List<BackupItemCategory>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new BackupItemCategory
            {
                Path = reader.GetString(0),
                Category = reader.GetString(1)
            });
        }

        return items;
    }

    private static async Task<IReadOnlyList<BackupLibraryFolder>> ReadLibraryFoldersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, display_name, is_enabled FROM library_folders ORDER BY path;";

        var folders = new List<BackupLibraryFolder>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new BackupLibraryFolder
            {
                Path = reader.GetString(0),
                DisplayName = reader.GetString(1),
                IsEnabled = reader.GetInt32(2) != 0
            });
        }

        return folders;
    }

    /// <summary>规则业务键：名称 + 正则 + 匹配目标（大小写不敏感）。</summary>
    private static string BuildRuleKey(string name, string pattern, int target) =>
        $"{name}\u0000{pattern}\u0000{target}";

    /// <summary>生成 @p0, @p1, … 形式的参数名列表（参数名由序号生成，不含外部数据）。</summary>
    private static string BuildParameterList(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $"@p{index}"));

    private static string ResolveAppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty;

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
            // 清理失败只影响磁盘上的一份临时文件，不改变导出结果。
        }
    }

    /// <summary>路径解析结果：原始路径 → 条目主键，以及未匹配清单。</summary>
    private sealed record ResolvedMediaIds(
        Dictionary<string, long> Map,
        IReadOnlyList<string> UnmatchedPaths);
}
