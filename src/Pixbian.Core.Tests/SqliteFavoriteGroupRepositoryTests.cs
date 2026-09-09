/**
 * 收藏分组仓储的集成测试。
 * 职责：针对真实 SQLite 文件验证分组的增删改名、成员关系写入与「未分组」查询语义。
 * 复用约定：每个用例在临时目录创建独立数据库并在结束时清理；
 *          媒体条目一律经 SqliteMediaItemRepository 写入，不手工拼 INSERT。
 * 关键约束：必须保留「入组即置收藏」「取消收藏后不再出现在分组查询里」「删条目不留关联残行」
 *          三条用例——它们守护的是收藏与分组的从属关系，一旦回归会出现查不到的幽灵成员。
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Pixbian.Core.Models;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>SqliteFavoriteGroupRepository 集成测试。</summary>
public sealed class SqliteFavoriteGroupRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteMediaItemRepository _mediaItems;
    private readonly SqliteFavoriteGroupRepository _groups;
    private readonly DateTimeOffset _fixedTime = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>创建临时数据库并完成迁移。</summary>
    public SqliteFavoriteGroupRepositoryTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "Pixbian.Tests",
            $"{Guid.NewGuid():N}.db");

        _initializer = new SqliteDatabaseInitializer(_databasePath);
        _initializer.Initialize();
        _mediaItems = new SqliteMediaItemRepository(_initializer.ConnectionString);
        _groups = new SqliteFavoriteGroupRepository(_initializer.ConnectionString);
    }

    /// <summary>删除临时数据库及其 WAL 附属文件。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

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
    public async Task AddAsync_新建分组_名称与排序写入()
    {
        var group = await _groups.AddAsync("旅行");

        Assert.True(group.Id > 0);
        Assert.Equal("旅行", group.Name);
        Assert.Equal(0, group.ItemCount);

        var all = await _groups.GetAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task AddAsync_重名_返回既有分组而非新建()
    {
        var first = await _groups.AddAsync("家人");
        var second = await _groups.AddAsync("家人");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _groups.GetAllAsync());
    }

    [Fact]
    public async Task RenameAsync_改名_名称更新()
    {
        var group = await _groups.AddAsync("旧名");

        await _groups.RenameAsync(group.Id, "新名");

        var renamed = (await _groups.GetAllAsync()).Single();
        Assert.Equal("新名", renamed.Name);
    }

    [Fact]
    public async Task SetMembershipAsync_加入分组_同时置为收藏()
    {
        var ids = await SeedItemsAsync(2);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);

        // 入组隐含收藏：分组查询带 is_favorite = 1，不置位则成员在分组里查不到。
        var inGroup = await _mediaItems.QueryAsync(new MediaQuery
        {
            FavoriteGroupId = group.Id,
            Take = 10
        });

        Assert.Equal(2, inGroup.Count);
        Assert.All(inGroup, item => Assert.True(item.IsFavorite));
    }

    [Fact]
    public async Task SetMembershipAsync_重复入组_幂等不报错()
    {
        var ids = await SeedItemsAsync(1);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);
        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);

        Assert.Equal(1, (await _groups.GetAllAsync()).Single().ItemCount);
    }

    [Fact]
    public async Task SetMembershipAsync_移出分组_保留收藏状态()
    {
        var ids = await SeedItemsAsync(1);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);
        await _groups.SetMembershipAsync(group.Id, ids, isMember: false);

        var item = await _mediaItems.GetByIdAsync(ids[0]);
        Assert.True(item!.IsFavorite);

        Assert.Empty(await _mediaItems.QueryAsync(new MediaQuery
        {
            FavoriteGroupId = group.Id,
            Take = 10
        }));
    }

    [Fact]
    public async Task QueryAsync_未分组_只取已收藏且无归属的条目()
    {
        var ids = await SeedItemsAsync(3);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, [ids[0]], isMember: true);

        var ungrouped = await _mediaItems.QueryAsync(new MediaQuery
        {
            IsFavorite = true,
            OnlyUngrouped = true,
            Take = 10
        });

        // 只有已加入分组的那条属于「已分组」，其余两条未收藏，故未分组查询应为空。
        Assert.Empty(ungrouped);

        await _mediaItems.SetFavoriteAsync([ids[1]], true);

        var afterSecondFavorite = await _mediaItems.QueryAsync(new MediaQuery
        {
            IsFavorite = true,
            OnlyUngrouped = true,
            Take = 10
        });

        Assert.Single(afterSecondFavorite);
        Assert.Equal(ids[1], afterSecondFavorite[0].Id);
    }

    [Fact]
    public async Task ClearMembershipAsync_清除归属_条目回到未分组()
    {
        var ids = await SeedItemsAsync(2);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);
        await _groups.ClearMembershipAsync(ids);

        Assert.Empty(await _mediaItems.QueryAsync(new MediaQuery
        {
            FavoriteGroupId = group.Id,
            Take = 10
        }));
    }

    [Fact]
    public async Task DeleteAsync_删除分组_条目保留收藏并回到未分组()
    {
        var ids = await SeedItemsAsync(1);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);
        await _groups.DeleteAsync(group.Id);

        Assert.Empty(await _groups.GetAllAsync());

        var ungrouped = await _mediaItems.QueryAsync(new MediaQuery
        {
            IsFavorite = true,
            OnlyUngrouped = true,
            Take = 10
        });

        Assert.Single(ungrouped);
        Assert.Equal(ids[0], ungrouped[0].Id);
    }

    [Fact]
    public async Task DeleteByIdsAsync_删除条目_不留分组关联残行()
    {
        var ids = await SeedItemsAsync(1);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);
        await _mediaItems.DeleteByIdsAsync(ids);

        Assert.Equal(0, await CountLinksAsync());
    }

    [Fact]
    public async Task GetGroupIdsByMediaAsync_按条目反查_返回所属分组()
    {
        var ids = await SeedItemsAsync(2);
        var first = await _groups.AddAsync("旅行");
        var second = await _groups.AddAsync("家人");

        await _groups.SetMembershipAsync(first.Id, [ids[0]], isMember: true);
        await _groups.SetMembershipAsync(second.Id, [ids[0]], isMember: true);

        var byMedia = await _groups.GetGroupIdsByMediaAsync(ids);

        Assert.Equal(2, byMedia[ids[0]].Count);
        Assert.Contains(first.Id, byMedia[ids[0]]);
        Assert.Contains(second.Id, byMedia[ids[0]]);
        Assert.False(byMedia.ContainsKey(ids[1]));
    }

    [Fact]
    public async Task GetAllAsync_成员计数_不含已取消收藏的条目()
    {
        var ids = await SeedItemsAsync(2);
        var group = await _groups.AddAsync("旅行");

        await _groups.SetMembershipAsync(group.Id, ids, isMember: true);
        Assert.Equal(2, (await _groups.GetAllAsync()).Single().ItemCount);

        await _mediaItems.SetFavoriteAsync([ids[0]], false);

        Assert.Equal(1, (await _groups.GetAllAsync()).Single().ItemCount);
    }

    /// <summary>写入若干媒体条目并返回其主键。</summary>
    /// <param name="count">条数。</param>
    private async Task<List<long>> SeedItemsAsync(int count)
    {
        List<MediaItem> items = [];

        for (var i = 0; i < count; i++)
        {
            var path = $"D:\\Lib\\favorite-{i}.jpg";

            items.Add(new MediaItem
            {
                Path = path,
                FileName = Path.GetFileName(path),
                Directory = Path.GetDirectoryName(path) ?? string.Empty,
                Kind = MediaKind.Image,
                FileSize = 1024,
                CreatedUtc = _fixedTime,
                ModifiedUtc = _fixedTime,
                IndexedUtc = _fixedTime,
                TakenUtc = _fixedTime
            });
        }

        await _mediaItems.UpsertBatchAsync(items);

        var ids = new List<long>();

        foreach (var item in items)
        {
            ids.Add(await QueryIdByPathAsync(item.Path));
        }

        return ids;
    }

    /// <summary>按路径取回主键。</summary>
    /// <param name="path">文件路径。</param>
    private async Task<long> QueryIdByPathAsync(string path)
    {
        var page = await _mediaItems.QueryAsync(new MediaQuery
        {
            DirectoryPath = Path.GetDirectoryName(path),
            Take = 100
        });

        return page.Single(i => string.Equals(i.Path, path, StringComparison.Ordinal)).Id;
    }

    /// <summary>统计分组关联表行数，用于验证删除条目后不留残行。</summary>
    private async Task<int> CountLinksAsync()
    {
        using var connection = new SqliteConnection(_initializer.ConnectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM favorite_group_items;";

        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
