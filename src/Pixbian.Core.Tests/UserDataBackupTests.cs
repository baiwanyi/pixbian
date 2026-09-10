/**
 * 用户数据备份服务（SqliteUserDataBackupService）的单元测试。
 * 职责：锁定导出与导入的核心契约——往返一致、重复导入幂等、未匹配路径容错、
 *      路径前缀重映射、坏文件与超限文件的拒绝、不安全正则的拦截，以及导出内容不含凭据。
 * 复用约定：每个用例自建临时库（SqliteDatabaseInitializer 建表）与临时备份文件，
 *          Dispose 时清理连接池、库文件（含 WAL 附属文件）与备份文件。
 * 关键约束：导入的原子性与正则安全校验是数据安全底线，相关用例不得删减或放宽为
 *          「不抛异常即可」；断言必须落到具体数据（收藏态、分组归属、分类与规则字段）。
 */

using Microsoft.Data.Sqlite;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Data.Backup;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>用户数据备份服务测试。</summary>
public sealed class UserDataBackupTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _temporaryFiles = [];

    /// <summary>清理全部临时库与备份文件。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in _temporaryFiles)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;

                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_导出的JSON不含凭据字段()
    {
        using var source = new TestDatabase(this);
        await SeedAsync(source);

        var backupPath = CreateBackupPath();
        await source.Backup.ExportAsync(backupPath, ["D:\\Music"]);

        var json = await File.ReadAllTextAsync(backupPath);

        // 备份包会同步到云端（OneDrive），绝不能带任何凭据；密码哈希由 DPAPI 绑定本机用户，
        // 跨机导入也必然失效，因此从一开始就不进备份模型。
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D:\\\\Music", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_导入到另一库_收藏分组分类规则与源库一致()
    {
        using var source = new TestDatabase(this);
        await SeedAsync(source);

        var backupPath = CreateBackupPath();
        await source.Backup.ExportAsync(backupPath, []);

        using var target = new TestDatabase(this);
        await SeedMediaAsync(target);

        var result = await target.Backup.ImportAsync(backupPath, new UserDataImportOptions());

        // 收藏与评分
        var items = await target.MediaItems.QueryAsync(new MediaQuery());
        Assert.Contains(items, item => item.FileName == "a.jpg" && item.IsFavorite && item.Rating == 4);
        Assert.Contains(items, item => item.FileName == "b.jpg" && item.IsFavorite);

        // 分类与规则
        var categories = await target.Categories.GetAllAsync();
        Assert.Contains(categories, category => category.Name == "风景" && category.Color == "#FF8800");

        var rules = await target.Rules.GetAllAsync();
        var rule = Assert.Single(rules);
        Assert.Equal("风景命名", rule.Name);
        Assert.Equal("^DSC_", rule.Pattern);
        Assert.Equal(RuleMatchTarget.FileName, rule.Target);

        // 分组与成员
        var groups = await target.Groups.GetAllAsync();
        var group = Assert.Single(groups);
        Assert.Equal("旅行", group.Name);
        Assert.Equal(1, group.ItemCount);

        Assert.Equal(2, result.Favorites);
        Assert.Equal(0, result.Unmatched);

        // 目标库为空：分类、规则、分组各新建一条。
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Updated);
    }

    [Fact]
    public async Task ImportAsync_重复导入同一文件_结果幂等()
    {
        using var database = new TestDatabase(this);
        await SeedMediaAsync(database);

        var backupPath = CreateBackupPath();
        using (var source = new TestDatabase(this))
        {
            await SeedAsync(source);
            await source.Backup.ExportAsync(backupPath, []);
        }

        await database.Backup.ImportAsync(backupPath, new UserDataImportOptions());
        var firstGroups = (await database.Groups.GetAllAsync()).Count;
        var firstRules = (await database.Rules.GetAllAsync()).Count;
        var firstCategories = (await database.Categories.GetAllAsync()).Count;

        // 第二次导入不得新增任何重复实体（按业务键去重）。
        await database.Backup.ImportAsync(backupPath, new UserDataImportOptions());

        Assert.Equal(firstGroups, (await database.Groups.GetAllAsync()).Count);
        Assert.Equal(firstRules, (await database.Rules.GetAllAsync()).Count);
        Assert.Equal(firstCategories, (await database.Categories.GetAllAsync()).Count);
    }

    [Fact]
    public async Task ImportAsync_未匹配路径_计入Unmatched且其余数据仍写入()
    {
        using var source = new TestDatabase(this);
        await SeedAsync(source);

        var backupPath = CreateBackupPath();
        await source.Backup.ExportAsync(backupPath, []);

        using var target = new TestDatabase(this);

        // 目标库只索引 a.jpg：b.jpg 已从磁盘与索引中消失。
        var a = CreateItem("D:\\Lib\\a.jpg");
        await target.MediaItems.UpsertBatchAsync([a]);

        var result = await target.Backup.ImportAsync(backupPath, new UserDataImportOptions());

        Assert.Equal(1, result.Favorites);
        Assert.Equal(1, result.Unmatched);
        Assert.Contains(result.UnmatchedPaths, path => path.EndsWith("b.jpg", StringComparison.OrdinalIgnoreCase));

        // 分类与规则不依赖媒体条目，必须照常导入。
        Assert.Single(await target.Categories.GetAllAsync());
        Assert.Single(await target.Rules.GetAllAsync());
    }

    [Fact]
    public async Task ImportAsync_路径前缀重映射_按新位置命中()
    {
        using var source = new TestDatabase(this);
        await SeedAsync(source);

        var backupPath = CreateBackupPath();
        await source.Backup.ExportAsync(backupPath, []);

        using var target = new TestDatabase(this);

        // 本机把媒体库从 D:\Lib 搬到了 E:\Media，路径前缀重映射后应能全部命中。
        await target.MediaItems.UpsertBatchAsync(
        [
            CreateItem("E:\\Media\\a.jpg"),
            CreateItem("E:\\Media\\b.jpg")
        ]);

        var options = new UserDataImportOptions
        {
            PathMappings = [new PathPrefixMapping("D:\\Lib", "E:\\Media")]
        };

        var result = await target.Backup.ImportAsync(backupPath, options);

        Assert.Equal(2, result.Favorites);
        Assert.Equal(0, result.Unmatched);
    }

    [Fact]
    public async Task ImportAsync_格式标识不匹配_拒绝且库保持原状()
    {
        using var database = new TestDatabase(this);
        await SeedMediaAsync(database);

        var backupPath = CreateBackupPath();
        await File.WriteAllTextAsync(
            backupPath,
            """{"format":"other.tool","formatVersion":1,"favorites":[{"path":"D:\\Lib\\a.jpg","rating":5}]}""");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => database.Backup.ImportAsync(backupPath, new UserDataImportOptions()));

        var items = await database.MediaItems.QueryAsync(new MediaQuery());
        Assert.All(items, item => Assert.False(item.IsFavorite));
    }

    [Fact]
    public async Task ImportAsync_版本高于当前_拒绝()
    {
        using var database = new TestDatabase(this);
        await SeedMediaAsync(database);

        var backupPath = CreateBackupPath();
        await File.WriteAllTextAsync(
            backupPath,
            """{"format":"pixbian.userdata","formatVersion":99}""");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => database.Backup.ImportAsync(backupPath, new UserDataImportOptions()));

        Assert.Contains("升级", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_非法JSON_拒绝()
    {
        using var database = new TestDatabase(this);

        var backupPath = CreateBackupPath();
        await File.WriteAllTextAsync(backupPath, "{ 这不是 JSON");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => database.Backup.ImportAsync(backupPath, new UserDataImportOptions()));
    }

    [Fact]
    public async Task ImportAsync_超大文件_拒绝()
    {
        using var database = new TestDatabase(this);

        var backupPath = CreateBackupPath();

        // 稀疏文件：只占元数据，不实际写入 65 MB 数据。
        using (var stream = new FileStream(backupPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength((64L * 1024 * 1024) + 1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => database.Backup.ImportAsync(backupPath, new UserDataImportOptions()));
    }

    [Fact]
    public async Task ImportAsync_非法正则_跳过该规则并计入SkippedRules()
    {
        using var database = new TestDatabase(this);
        await SeedMediaAsync(database);

        var backupPath = CreateBackupPath();
        await File.WriteAllTextAsync(
            backupPath,
            """
            {
              "format": "pixbian.userdata",
              "formatVersion": 1,
              "categories": [{ "name": "截图", "sortOrder": 1, "isEnabled": true }],
              "rules": [
                { "name": "坏正则", "pattern": "([a-z", "category": "截图", "target": "fileName" },
                { "name": "正常", "pattern": "^Screenshot_", "category": "截图", "target": "fileName" }
              ]
            }
            """);

        var result = await database.Backup.ImportAsync(backupPath, new UserDataImportOptions());

        // 规则正则来自外部文件，属不可信输入：语法非法（超长同理）的整条跳过，
        // 同文件中的正常规则照常导入。运行期灾难性回溯由 CategoryRuleHelper 的
        // MatchTimeout 兜底，此处只保证不会把明显非法的模式写进库。
        // Added 为分类 1 + 规则 1：分类先建，规则再挂到它上面。
        Assert.Equal(1, result.SkippedRules);
        Assert.Equal(2, result.Added);

        var rules = await database.Rules.GetAllAsync();
        var rule = Assert.Single(rules);
        Assert.Equal("正常", rule.Name);
    }

    /// <summary>创建本次测试专用的备份文件路径，并登记以便 Dispose 清理。</summary>
    private string CreateBackupPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", $"{Guid.NewGuid():N}.userdata.json");
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _temporaryFiles.Add(path);
        return path;
    }

    /// <summary>写入两个收藏条目、一个分类、一条规则、一个收藏分组与一个扫描源。</summary>
    private static async Task SeedAsync(TestDatabase database)
    {
        await SeedMediaAsync(database);

        var items = await database.MediaItems.QueryAsync(new MediaQuery());
        var a = items.First(item => item.FileName == "a.jpg");
        var b = items.First(item => item.FileName == "b.jpg");

        await database.MediaItems.SetFavoriteAsync([a.Id], true);
        await database.MediaItems.SetFavoriteAsync([b.Id], true);
        await SetRatingAsync(database, a.Id, 4);

        var category = await database.Categories.AddAsync("风景", "#FF8800");
        await database.Rules.AddAsync(new CategoryRule
        {
            Name = "风景命名",
            Pattern = "^DSC_",
            CategoryId = category.Id,
            Target = RuleMatchTarget.FileName,
            Priority = 5
        });

        var group = await database.Groups.AddAsync("旅行");
        await database.Groups.SetMembershipAsync(group.Id, [a.Id], true);

        await database.Folders.AddAsync("D:\\Lib", "Lib");
    }

    /// <summary>写入两个媒体条目（不置收藏）。</summary>
    private static Task SeedMediaAsync(TestDatabase database) =>
        database.MediaItems.UpsertBatchAsync(
        [
            CreateItem("D:\\Lib\\a.jpg"),
            CreateItem("D:\\Lib\\b.jpg")
        ]);

    /// <summary>直接写库设置评分：仓储接口只暴露收藏状态，评分用于验证导入时的「只升不降」语义。</summary>
    private static async Task SetRatingAsync(TestDatabase database, long id, int rating)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media_items SET rating = @rating WHERE id = @id;";
        command.Parameters.AddWithValue("@rating", rating);
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync();
    }

    private static MediaItem CreateItem(string path) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path) ?? string.Empty,
        Kind = MediaKind.Image,
        FileSize = 1024,
        CreatedUtc = FixedTime,
        ModifiedUtc = FixedTime,
        IndexedUtc = FixedTime
    };

    /// <summary>临时库与相关仓储/服务的组合，供各用例复用。</summary>
    private sealed class TestDatabase : IDisposable
    {
        public TestDatabase(UserDataBackupTests owner)
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", $"{Guid.NewGuid():N}.db");
            owner._temporaryFiles.Add(DatabasePath);

            var initializer = new SqliteDatabaseInitializer(DatabasePath);
            initializer.Initialize();

            ConnectionString = initializer.ConnectionString;
            MediaItems = new SqliteMediaItemRepository(ConnectionString);
            Categories = new SqliteCategoryRepository(ConnectionString);
            Rules = new SqliteCategoryRuleRepository(ConnectionString);
            Groups = new SqliteFavoriteGroupRepository(ConnectionString);
            Folders = new SqliteLibraryFolderRepository(ConnectionString);
            Backup = new SqliteUserDataBackupService(ConnectionString);
        }

        public string DatabasePath { get; }

        public string ConnectionString { get; }

        public SqliteMediaItemRepository MediaItems { get; }

        public SqliteCategoryRepository Categories { get; }

        public SqliteCategoryRuleRepository Rules { get; }

        public SqliteFavoriteGroupRepository Groups { get; }

        public SqliteLibraryFolderRepository Folders { get; }

        public SqliteUserDataBackupService Backup { get; }

        /// <summary>清理连接池，使库文件可被删除（实际删除由外层 Dispose 统一处理）。</summary>
        public void Dispose() => SqliteConnection.ClearAllPools();
    }
}
