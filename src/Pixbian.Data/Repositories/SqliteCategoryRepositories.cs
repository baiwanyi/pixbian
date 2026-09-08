/**
 * 分类与分类规则的 SQLite 仓储实现（M5）。
 * 职责：实现分类实体的增删改查、规则的持久化、优先级重排与命中结果批量写回。
 * 复用约定：全部使用参数化查询；每次操作独立连接并依赖连接池；时间以 ISO8601 往返格式存取。
 * 关键约束：ApplyMatches 必须把未命中的条目显式置空，否则规则调整后旧分类不会撤销；
 *          优先级重排须在单事务内完成，中途失败会留下乱序的规则集合；
 *          IN 子句的参数名由序号生成，不含外部数据，拼接是安全的。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;

namespace Pixbian.Data.Repositories;

/// <summary>分类的 SQLite 仓储。</summary>
public sealed class SqliteCategoryRepository : ICategoryRepository
{
    private const string SelectColumns = "SELECT id, name, color, sort_order, is_enabled FROM categories";

    private readonly string _connectionString;

    /// <summary>初始化仓储。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    public SqliteCategoryRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY sort_order, name;";

        var categories = new List<Category>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(MapCategory(reader));
        }

        return categories;
    }

    /// <inheritdoc />
    public async Task<Category> AddAsync(
        string name,
        string? color = null,
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
            INSERT INTO categories (name, color, sort_order)
            VALUES (@name, @color, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM categories))
            RETURNING id, name, color, sort_order, is_enabled;
            """;

        command.Parameters.AddWithValue("@name", name.Trim());
        command.Parameters.AddWithValue("@color", (object?)color ?? DBNull.Value);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"分类写入失败：{name}");
        }

        return MapCategory(reader);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE categories SET name = @name, color = @color WHERE id = @id;";

        command.Parameters.AddWithValue("@name", category.Name.Trim());
        command.Parameters.AddWithValue("@color", (object?)category.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", category.Id);

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
        command.CommandText = "UPDATE categories SET is_enabled = @is_enabled WHERE id = @id;";
        command.Parameters.AddWithValue("@is_enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM categories WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Category?> FindByNameAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE name = @name;";
        command.Parameters.AddWithValue("@name", name.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapCategory(reader)
            : null;
    }

    private static Category MapCategory(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Color = reader.IsDBNull(2) ? null : reader.GetString(2),
        SortOrder = reader.GetInt32(3),
        IsEnabled = reader.GetInt32(4) != 0
    };
}

/// <summary>分类规则的 SQLite 仓储。</summary>
public sealed class SqliteCategoryRuleRepository : ICategoryRuleRepository
{
    private const string SelectColumns = """
        SELECT id, name, pattern, category_id, target, is_enabled, priority, is_case_sensitive
        FROM category_rules
        """;

    private readonly string _connectionString;

    /// <summary>初始化仓储。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    public SqliteCategoryRuleRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CategoryRule>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY priority DESC, id;";

        return await ReadRulesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CategoryRule>> GetEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Join 分类过滤禁用分类：分类禁用即其下全部规则退出匹配，规则自身开关仍是第一道筛选。
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.id, r.name, r.pattern, r.category_id, r.target, r.is_enabled, r.priority, r.is_case_sensitive
            FROM category_rules AS r
            INNER JOIN categories AS c ON c.id = r.category_id
            WHERE r.is_enabled = 1 AND c.is_enabled = 1
            ORDER BY r.priority DESC, r.id;
            """;

        return await ReadRulesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CategoryRule> AddAsync(
        CategoryRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO category_rules
                (name, pattern, category_id, target, is_enabled, priority, is_case_sensitive)
            VALUES
                (@name, @pattern, @category_id, @target, @is_enabled, @priority, @is_case_sensitive)
            RETURNING id, name, pattern, category_id, target, is_enabled, priority, is_case_sensitive;
            """;

        BindRule(command, rule);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"规则写入失败：{rule.Name}");
        }

        return MapRule(reader);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(CategoryRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE category_rules SET
                name = @name,
                pattern = @pattern,
                category_id = @category_id,
                target = @target,
                is_enabled = @is_enabled,
                priority = @priority,
                is_case_sensitive = @is_case_sensitive
            WHERE id = @id;
            """;

        BindRule(command, rule);
        command.Parameters.AddWithValue("@id", rule.Id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM category_rules WHERE id = @id;";
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
        command.CommandText = "UPDATE category_rules SET is_enabled = @is_enabled WHERE id = @id;";
        command.Parameters.AddWithValue("@is_enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdatePrioritiesAsync(
        IReadOnlyList<long> orderedRuleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedRuleIds);

        if (orderedRuleIds.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 单事务完成整体重排，中途失败不会留下半新半旧的优先级。
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < orderedRuleIds.Count; index++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE category_rules SET priority = @priority WHERE id = @id;";

            // 列表首位优先级最高，故用倒序数值，使数值越大越先匹配。
            command.Parameters.AddWithValue("@priority", orderedRuleIds.Count - index);
            command.Parameters.AddWithValue("@id", orderedRuleIds[index]);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApplyMatchesAsync(
        IReadOnlyList<RuleMatchResult> matches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Count == 0)
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
        command.CommandText = "UPDATE media_items SET category_id = @category_id WHERE id = @id;";

        var idParam = command.Parameters.Add("@id", SqliteType.Integer);
        var categoryParam = command.Parameters.Add("@category_id", SqliteType.Integer);

        foreach (var match in matches)
        {
            idParam.Value = match.MediaId;

            // 未命中时显式置空，确保规则调整后旧的分类归属会被撤销。
            categoryParam.Value = match.CategoryId.HasValue
                ? match.CategoryId.Value
                : DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindRule(SqliteCommand command, CategoryRule rule)
    {
        command.Parameters.AddWithValue("@name", rule.Name);
        command.Parameters.AddWithValue("@pattern", rule.Pattern);
        command.Parameters.AddWithValue("@category_id", rule.CategoryId);
        command.Parameters.AddWithValue("@target", (int)rule.Target);
        command.Parameters.AddWithValue("@is_enabled", rule.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@priority", rule.Priority);
        command.Parameters.AddWithValue("@is_case_sensitive", rule.IsCaseSensitive ? 1 : 0);
    }

    private static async Task<IReadOnlyList<CategoryRule>> ReadRulesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rules = new List<CategoryRule>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(MapRule(reader));
        }

        return rules;
    }

    private static CategoryRule MapRule(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Pattern = reader.GetString(2),
        CategoryId = reader.GetInt64(3),
        Target = (RuleMatchTarget)reader.GetInt32(4),
        IsEnabled = reader.GetInt32(5) != 0,
        Priority = reader.GetInt32(6),
        IsCaseSensitive = reader.GetInt32(7) != 0
    };
}
