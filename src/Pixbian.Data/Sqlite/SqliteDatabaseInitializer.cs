/**
 * SQLite 数据库初始化器。
 * 职责：创建数据库文件所在目录、打开连接、应用运行时 PRAGMA，并按序执行未应用的 Schema 迁移。
 * 复用约定：连接字符串由 Microsoft.Data.Sqlite 的连接字符串构建器生成，避免手工拼接；
 *          每次操作使用独立连接并依赖连接池复用，天然线程安全。
 * 关键约束：WAL 模式必须启用，否则后台索引写入会阻塞 UI 线程的读取查询；
 *          busy_timeout 必须设置，并发写入时否则会立即抛出 database is locked；
 *          不得启用 SqliteCacheMode.Shared——该特性已弃用，其锁定语义与 busy_timeout 冲突；
 *          PRAGMA user_version 不接受参数化，版本号为内部常量（long 强类型），非用户可控输入。
 */

using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pixbian.Data.Sqlite;

/// <summary>SQLite 数据库初始化器。</summary>
public sealed partial class SqliteDatabaseInitializer
{
    private const int BusyTimeoutMilliseconds = 5000;

    // 日志使用 LoggerMessage 源生成器，避免每次调用都解析消息模板并装箱参数（CA1848）。
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "数据库已就绪，当前 Schema 版本 {Version}。")]
    private static partial void LogReady(ILogger logger, long version);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information,
        Message = "已应用数据库迁移 v{Version}。")]
    private static partial void LogMigrationApplied(ILogger logger, int version);

    private readonly ILogger _logger;

    /// <summary>初始化数据库初始化器。</summary>
    /// <param name="databasePath">数据库文件完整路径。</param>
    /// <param name="logger">日志记录器；为空时使用空实现。</param>
    public SqliteDatabaseInitializer(string databasePath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 不启用 SqliteCacheMode.Shared：共享缓存是 Microsoft.Data.Sqlite 的已弃用特性，
            // 其 SQLITE_LOCKED 语义与 busy_timeout 不一致，会放大并发写的锁冲突并抵消 WAL 的收益。
            // 每次操作独立连接 + 连接池（Pooling）已能满足本项目的访问方式。
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>可用于打开数据库连接的连接字符串。</summary>
    public string ConnectionString { get; }

    /// <summary>创建数据库并执行全部未应用的迁移。</summary>
    public void Initialize()
    {
        EnsureParentDirectoryExists();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        ApplyPragmas(connection);
        ApplyMigrations(connection);

        LogReady(_logger, GetUserVersion(connection));
    }

    /// <summary>确保数据库文件所在目录存在。</summary>
    private void EnsureParentDirectoryExists()
    {
        var builder = new SqliteConnectionStringBuilder(ConnectionString);
        var dataSource = builder.DataSource;
        var directory = Path.GetDirectoryName(dataSource);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>应用运行时 PRAGMA。</summary>
    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = {BusyTimeoutMilliseconds};
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>按序应用版本号高于当前版本的迁移。</summary>
    private void ApplyMigrations(SqliteConnection connection)
    {
        var currentVersion = GetUserVersion(connection);

        foreach (var migration in SchemaMigrations.All
                     .Where(m => m.Version > currentVersion)
                     .OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction();

            foreach (var statement in migration.Statements)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            SetUserVersion(connection, transaction, migration.Version);
            transaction.Commit();

            LogMigrationApplied(_logger, migration.Version);
        }
    }

    /// <summary>读取 PRAGMA user_version。</summary>
    private static long GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>写入 PRAGMA user_version。</summary>
    private static void SetUserVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // PRAGMA 语句不支持参数绑定。版本号为本程序集内定义的常量且经 long 强类型约束，
        // 不存在外部可控输入，故此处拼接是安全的。
        command.CommandText = "PRAGMA user_version = "
            + version.ToString(CultureInfo.InvariantCulture)
            + ";";

        command.ExecuteNonQuery();
    }
}
