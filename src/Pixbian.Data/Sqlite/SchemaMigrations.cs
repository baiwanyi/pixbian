/**
 * 数据库 Schema 定义与迁移脚本集合。
 * 职责：以版本化迁移脚本的形式维护索引库结构，供初始化器按序应用。
 * 复用约定：版本以 PRAGMA user_version 记录，迁移在单个事务内原子执行，中断后重启可续跑；
 *          时间统一存 ISO8601 往返格式文本，布尔存 0/1 整数，枚举存整数。
 * 关键约束：已发布的迁移脚本禁止修改，结构变更必须新增更高版本，否则既有库无法正确升级；
 *          media_items.is_favorite、rating、category_id 属于用户数据，Upsert 的冲突更新子句必须排除它们。
 */

namespace Pixbian.Data.Sqlite;

/// <summary>单条数据库迁移。</summary>
/// <param name="Version">目标版本号，须严格递增。</param>
/// <param name="Statements">按序执行的 SQL 语句集合。</param>
public sealed record SchemaMigration(int Version, IReadOnlyList<string> Statements);

/// <summary>Schema 迁移脚本集合。</summary>
public static class SchemaMigrations
{
    /// <summary>当前最新版本号。</summary>
    public const int CurrentVersion = 7;

    /// <summary>全部迁移脚本，按版本号升序。</summary>
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new SchemaMigration(1, SchemaV1.Statements),
        new SchemaMigration(2, SchemaV2.Statements),
        new SchemaMigration(3, SchemaV3.Statements),
        new SchemaMigration(4, SchemaV4.Statements),
        new SchemaMigration(5, SchemaV5.Statements),
        new SchemaMigration(6, SchemaV6.Statements),
        new SchemaMigration(7, SchemaV7.Statements)
    ];
}

/// <summary>Schema v1：媒体条目、扫描源、分类、规则与标签。</summary>
public static class SchemaV1
{
    /// <summary>v1 的全部建表语句。</summary>
    public static IReadOnlyList<string> Statements { get; } =
    [
        """
        CREATE TABLE IF NOT EXISTS media_items (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            path         TEXT    NOT NULL UNIQUE,
            file_name    TEXT    NOT NULL,
            directory    TEXT    NOT NULL,
            kind         INTEGER NOT NULL,
            file_size    INTEGER NOT NULL,
            created_utc  TEXT    NOT NULL,
            modified_utc TEXT    NOT NULL,
            indexed_utc  TEXT    NOT NULL,
            taken_utc    TEXT    NULL,
            width        INTEGER NULL,
            height       INTEGER NULL,
            duration_ms  INTEGER NULL,
            is_favorite  INTEGER NOT NULL DEFAULT 0,
            category_id  INTEGER NULL,
            rating       INTEGER NOT NULL DEFAULT 0,
            deleted_utc  TEXT    NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS library_folders (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            path          TEXT    NOT NULL UNIQUE,
            display_name  TEXT    NOT NULL,
            added_utc     TEXT    NOT NULL,
            is_enabled    INTEGER NOT NULL DEFAULT 1,
            last_scan_utc TEXT    NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS categories (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT    NOT NULL UNIQUE,
            color      TEXT    NULL,
            sort_order INTEGER NOT NULL DEFAULT 0
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS category_rules (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            name              TEXT    NOT NULL,
            pattern           TEXT    NOT NULL,
            category_id       INTEGER NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
            target            INTEGER NOT NULL DEFAULT 0,
            is_enabled        INTEGER NOT NULL DEFAULT 1,
            priority          INTEGER NOT NULL DEFAULT 0,
            is_case_sensitive INTEGER NOT NULL DEFAULT 0
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS tags (
            id   INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS media_tags (
            media_id INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
            tag_id   INTEGER NOT NULL REFERENCES tags(id)        ON DELETE CASCADE,
            PRIMARY KEY (media_id, tag_id)
        );
        """,

        "CREATE INDEX IF NOT EXISTS ix_media_items_kind      ON media_items(kind);",
        "CREATE INDEX IF NOT EXISTS ix_media_items_taken     ON media_items(taken_utc DESC);",
        "CREATE INDEX IF NOT EXISTS ix_media_items_directory ON media_items(directory);",
        "CREATE INDEX IF NOT EXISTS ix_media_items_category  ON media_items(category_id);",
        "CREATE INDEX IF NOT EXISTS ix_media_items_size      ON media_items(file_size);",
        "CREATE INDEX IF NOT EXISTS ix_category_rules_enabled ON category_rules(is_enabled, priority DESC);"
    ];
}

/// <summary>Schema v2：媒体条目的元数据探测状态，供后台回填推进。</summary>
public static class SchemaV2
{
    /// <summary>v2 的全部变更语句。</summary>
    /// <remarks>
    /// 探测状态分三态：未探测 / 已完成 / 已失败。失败必须单独成态而非回落到未探测，
    /// 否则损坏或不受支持的文件会在每轮回填里被反复捞取，永远占满批次额度。
    /// 待处理索引用部分索引：回填推进时命中的行越来越少，全部完成后索引近乎为空，
    /// 既省空间也让「还有多少待处理」的查询始终走索引而非全表扫描。
    /// </remarks>
    public static IReadOnlyList<string> Statements { get; } =
    [
        "ALTER TABLE media_items ADD COLUMN metadata_state INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE media_items ADD COLUMN metadata_utc    TEXT    NULL;",

        "CREATE INDEX IF NOT EXISTS ix_media_items_metadata_pending "
            + "ON media_items(id) WHERE metadata_state = 0;"
    ];
}

/// <summary>Schema v3：规模化查询索引。modified_utc 与 file_name 是默认排序与名字排序的排序键，
/// 此前无索引支撑，每次分页查询都退化为全表排序；条目数向数十万级增长后，全表排序
/// 是查询耗时的首要来源。SQLite 可反向扫描索引，单列升序索引同时服务升降两个方向。</summary>
public static class SchemaV3
{
    /// <summary>v3 的全部变更语句。</summary>
    public static IReadOnlyList<string> Statements { get; } =
    [
        "CREATE INDEX IF NOT EXISTS ix_media_items_modified  ON media_items(modified_utc);",
        "CREATE INDEX IF NOT EXISTS ix_media_items_file_name ON media_items(file_name);"
    ];
}

/// <summary>Schema v4：固定随机序列。把「主键乘种子取模」的随机排序从 ORDER BY 表达式
/// （无法走索引、每次查询全表计算）改为入库时生成一次、永久不变的 random_rank 列：
/// 排序走索引扫描，分页可基于游标（rank &gt;= 上一页末条）而非 OFFSET 深翻。
/// rank 由 id 经乘法哈希对素数取模生成，对应用不可变；新条目由 AFTER INSERT 触发器填充。</summary>
public static class SchemaV4
{
    /// <summary>v4 的全部变更语句。</summary>
    /// <remarks>
    /// 乘数 2654435761 为 Knuth 乘法哈希常数；模数 2147483647（2^31-1）为素数，
    /// id 在 64 位整数内乘法不溢出，结果域 [0, 2147483646]。存量行由迁移内一次回填，
    /// 此后 rank 与 id 绑定不再变化，任何一次随机浏览的顺序在全库范围内稳定。
    /// </remarks>
    public static IReadOnlyList<string> Statements { get; } =
    [
        "ALTER TABLE media_items ADD COLUMN random_rank INTEGER NULL;",
        "UPDATE media_items SET random_rank = (id * 2654435761) % 2147483647 WHERE random_rank IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_media_items_random ON media_items(random_rank);",
        """
        CREATE TRIGGER IF NOT EXISTS trg_media_items_random_rank
        AFTER INSERT ON media_items
        BEGIN
            UPDATE media_items
            SET random_rank = (NEW.id * 2654435761) % 2147483647
            WHERE id = NEW.id;
        END;
        """
    ];
}

/// <summary>Schema v5：音乐库曲目表。短片页在视频无音轨时需从音乐库随机抽曲作为背景音乐，
/// 曲目单独建表而非复用 media_items：音频一旦进入 media_items，图库在 Kind 为空（不过滤类型）
/// 时会把它连同图片与视频一并查出，且会被元数据回填反复探测。
/// 规模远小于图库（数百至数千条），故采用「全量替换」式对账，不设增量状态列。</summary>
public static class SchemaV5
{
    /// <summary>v5 的全部变更语句。</summary>
    public static IReadOnlyList<string> Statements { get; } =
    [
        """
        CREATE TABLE IF NOT EXISTS music_tracks (
            path      TEXT    NOT NULL PRIMARY KEY,
            file_name TEXT    NOT NULL,
            directory TEXT    NOT NULL,
            file_size INTEGER NOT NULL,
            added_utc TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_music_tracks_directory ON music_tracks(directory);"
    ];
}

/// <summary>Schema v6：分类的启用状态。禁用分类只使其下规则退出自动匹配，
/// 已有归属与导航展示不受影响；默认 1 保证存量分类行为不变。</summary>
public static class SchemaV6
{
    /// <summary>v6 的全部变更语句。</summary>
    public static IReadOnlyList<string> Statements { get; } =
    [
        "ALTER TABLE categories ADD COLUMN is_enabled INTEGER NOT NULL DEFAULT 1;"
    ];
}

/// <summary>Schema v7：收藏分组。分组与媒体是多对多关系，故单独建关联表而不用
/// media_items 上的分组列：一条目可同时归入多个分组，单列无法表达且会让「入组」与
/// 「移出」退化为覆盖写。关联表两端均带 ON DELETE CASCADE，条目或分组被删都不留残行。
/// 「未分组」不作为记录存在，它是「已收藏且无关联行」的查询语义。</summary>
public static class SchemaV7
{
    /// <summary>v7 的全部变更语句。</summary>
    public static IReadOnlyList<string> Statements { get; } =
    [
        """
        CREATE TABLE IF NOT EXISTS favorite_groups (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT    NOT NULL UNIQUE,
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT   NOT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS favorite_group_items (
            media_id   INTEGER NOT NULL REFERENCES media_items(id)     ON DELETE CASCADE,
            group_id   INTEGER NOT NULL REFERENCES favorite_groups(id) ON DELETE CASCADE,
            added_utc  TEXT    NOT NULL,
            PRIMARY KEY (media_id, group_id)
        );
        """,

        // 按分组取成员是主要查询方向（侧栏点分组即走此索引）；反向按条目查归属
        // 由主键索引（media_id, group_id）天然覆盖，无需再建。
        "CREATE INDEX IF NOT EXISTS ix_favorite_group_items_group ON favorite_group_items(group_id);"
    ];
}
