/**
 * 分类与规则仓储的集成测试（M5）。
 * 职责：验证分类的幂等添加、规则的增删改、优先级重排、命中结果写回与分类筛选。
 * 复用约定：真实 SQLite 文件、独立临时数据库、固定时间戳；与既有的仓储测试保持一致。
 * 关键约束：必须保留「未命中时显式清空旧分类」用例——
 *          若只写命中的条目，规则调整后旧的错误归类会永久残留；
 *          以及「优先级整体重排」用例，重排须在单事务内完成且列表首位优先级最高。
 */

using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>分类与规则仓储的集成测试。</summary>
public sealed class CategoryRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteCategoryRepository _categories;
    private readonly SqliteCategoryRuleRepository _rules;
    private readonly SqliteMediaItemRepository _mediaItems;

    /// <summary>创建临时数据库并完成迁移。</summary>
    public CategoryRepositoryTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "Pixbian.Tests",
            $"{Guid.NewGuid():N}.db");

        var initializer = new SqliteDatabaseInitializer(_databasePath);
        initializer.Initialize();

        var connectionString = initializer.ConnectionString;
        _categories = new SqliteCategoryRepository(connectionString);
        _rules = new SqliteCategoryRuleRepository(connectionString);
        _mediaItems = new SqliteMediaItemRepository(connectionString);
    }

    /// <summary>清理临时数据库及其 WAL 附属文件。</summary>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _databasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task AddAsync_同名分类_返回既有记录而不重复创建()
    {
        var first = await _categories.AddAsync("手机照片");
        var second = await _categories.AddAsync("手机照片");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _categories.GetAllAsync());
    }

    [Fact]
    public async Task GetAllAsync_按排序序号升序返回()
    {
        await _categories.AddAsync("B");
        await _categories.AddAsync("A");

        var all = await _categories.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.True(all[0].SortOrder <= all[1].SortOrder);
    }

    [Fact]
    public async Task DeleteAsync_删除分类_级联删除其下规则()
    {
        var category = await _categories.AddAsync("待删除");
        await _rules.AddAsync(CreateRule("规则", @"IMG_", category.Id));

        await _categories.DeleteAsync(category.Id);

        Assert.Empty(await _categories.GetAllAsync());
        Assert.Empty(await _rules.GetAllAsync());
    }

    [Fact]
    public async Task GetEnabledAsync_仅返回已启用规则()
    {
        var category = await _categories.AddAsync("分类");
        await _rules.AddAsync(CreateRule("启用", @"IMG_", category.Id));
        var disabled = await _rules.AddAsync(CreateRule("禁用", @"VID_", category.Id));
        await _rules.SetEnabledAsync(disabled.Id, false);

        var enabled = await _rules.GetEnabledAsync();

        Assert.Single(enabled);
        Assert.Equal("启用", enabled[0].Name);
    }

    [Fact]
    public async Task UpdatePrioritiesAsync_列表首位获得最高优先级()
    {
        var category = await _categories.AddAsync("分类");
        var a = await _rules.AddAsync(CreateRule("A", @"IMG_", category.Id));
        var b = await _rules.AddAsync(CreateRule("B", @"VID_", category.Id));

        await _rules.UpdatePrioritiesAsync([b.Id, a.Id]);

        var all = await _rules.GetAllAsync();

        Assert.Equal("B", all[0].Name);
        Assert.True(all[0].Priority > all[1].Priority);
    }

    [Fact]
    public async Task ApplyMatchesAsync_写入命中分类()
    {
        var category = await _categories.AddAsync("分类");
        await _mediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\IMG_1.jpg")]);

        var item = (await _mediaItems.QueryAsync(new MediaQuery())).Single();
        await _rules.ApplyMatchesAsync([new RuleMatchResult(item.Id, category.Id, 1)]);

        var updated = (await _mediaItems.QueryAsync(new MediaQuery())).Single();
        Assert.Equal(category.Id, updated.CategoryId);
    }

    [Fact]
    public async Task ApplyMatchesAsync_未命中时清空旧归类()
    {
        var category = await _categories.AddAsync("分类");
        await _mediaItems.UpsertBatchAsync([CreateItem("D:\\Lib\\IMG_1.jpg")]);

        var item = (await _mediaItems.QueryAsync(new MediaQuery())).Single();

        await _rules.ApplyMatchesAsync([new RuleMatchResult(item.Id, category.Id, 1)]);
        Assert.NotNull((await _mediaItems.QueryAsync(new MediaQuery())).Single().CategoryId);

        // 规则调整后该条目不再命中，旧归类必须被撤销。
        await _rules.ApplyMatchesAsync([new RuleMatchResult(item.Id, null, null)]);

        Assert.Null((await _mediaItems.QueryAsync(new MediaQuery())).Single().CategoryId);
    }

    [Fact]
    public async Task QueryAsync_按分类筛选_仅返回该分类条目()
    {
        var phones = await _categories.AddAsync("手机");
        var screenshots = await _categories.AddAsync("截图");

        await _mediaItems.UpsertBatchAsync([
            CreateItem("D:\\Lib\\IMG_1.jpg"),
            CreateItem("D:\\Lib\\shot.png")
        ]);

        var items = await _mediaItems.QueryAsync(new MediaQuery());
        await _rules.ApplyMatchesAsync([
            new RuleMatchResult(items[0].Id, phones.Id, 1),
            new RuleMatchResult(items[1].Id, screenshots.Id, 2)
        ]);

        var filtered = await _mediaItems.QueryAsync(new MediaQuery { CategoryId = phones.Id });

        Assert.Single(filtered);
        Assert.Equal("IMG_1.jpg", filtered[0].FileName);
    }

    [Fact]
    public async Task ApplyMatchesAsync_超大批次_全部写入()
    {
        var category = await _categories.AddAsync("分类");

        await _mediaItems.UpsertBatchAsync(Enumerable.Range(0, 900)
            .Select(i => CreateItem($"D:\\Lib\\f{i}.jpg"))
            .ToList());

        var items = await _mediaItems.QueryAsync(new MediaQuery { Take = 900 });
        var matches = items.Select(i => new RuleMatchResult(i.Id, category.Id, 1)).ToList();

        await _rules.ApplyMatchesAsync(matches);

        var filtered = await _mediaItems.QueryAsync(new MediaQuery { CategoryId = category.Id, Take = 900 });
        Assert.Equal(900, filtered.Count);
    }

    private static CategoryRule CreateRule(string name, string pattern, long categoryId) => new()
    {
        Name = name,
        Pattern = pattern,
        CategoryId = categoryId,
        Target = RuleMatchTarget.FileName,
        IsEnabled = true,
        Priority = 0
    };

    private static MediaItem CreateItem(string path) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
        Kind = MediaKind.Image,
        FileSize = 1024,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        IndexedUtc = DateTimeOffset.UnixEpoch
    };
}
