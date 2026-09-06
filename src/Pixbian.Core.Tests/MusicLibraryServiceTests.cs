/**
 * 音乐库服务的单元测试。
 * 职责：覆盖目录扫描的白名单过滤、目录缺失与未配置的降级、全量替换式对账，以及随机抽曲。
 * 复用约定：仓储与设置用手写桩件而非模拟框架——本测试项目未引入模拟库，且桩件足以表达契约。
 * 关键约束：扫描用例必须落在真实临时目录里（EnumerateFiles 与 FileInfo 依赖真实文件系统），
 *           并在 finally 中清理，避免并行测试相互干扰与临时目录堆积。
 */

using System.IO;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>MusicLibraryService 测试。</summary>
public sealed class MusicLibraryServiceTests
{
    [Fact]
    public async Task ScanAsync_只收录受支持的音频文件()
    {
        var root = CreateTempDirectory();

        try
        {
            CreateFile(root, "a.mp3");
            CreateFile(root, "b.flac");
            CreateFile(root, "cover.jpg");
            CreateFile(root, "notes.txt");

            var repository = new StubMusicTrackRepository();
            var service = CreateService(repository, root);

            var count = await service.ScanAsync();

            Assert.Equal(2, count);
            Assert.Contains(repository.Tracks, t => t.FileName == "a.mp3");
            Assert.Contains(repository.Tracks, t => t.FileName == "b.flac");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_递归子目录()
    {
        var root = CreateTempDirectory();

        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "Album", "Disc1")).FullName;
            CreateFile(nested, "deep.m4a");

            var repository = new StubMusicTrackRepository();
            var service = CreateService(repository, root);

            Assert.Equal(1, await service.ScanAsync());
            Assert.Equal(nested, repository.Tracks[0].Directory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_目录不存在_跳过而不抛出()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pixbian-music-missing-" + Guid.NewGuid().ToString("N"));
        var repository = new StubMusicTrackRepository();
        var service = CreateService(repository, missing);

        var count = await service.ScanAsync();

        Assert.Equal(0, count);
        Assert.Empty(repository.Tracks);
    }

    [Fact]
    public async Task ScanAsync_未配置目录_结果为空()
    {
        var repository = new StubMusicTrackRepository();
        var service = new MusicLibraryService(repository, new StubSettingsService([]));

        Assert.Equal(0, await service.ScanAsync());
        Assert.Empty(repository.Tracks);
    }

    [Fact]
    public async Task ScanAsync_文件被删除后重扫_旧记录随之消失()
    {
        var root = CreateTempDirectory();

        try
        {
            CreateFile(root, "a.mp3");
            CreateFile(root, "b.mp3");

            var repository = new StubMusicTrackRepository();
            var service = CreateService(repository, root);

            Assert.Equal(2, await service.ScanAsync());

            File.Delete(Path.Combine(root, "b.mp3"));

            // 全量替换是对账的唯一手段：磁盘上消失的文件必须在这次扫描后不再出现。
            Assert.Equal(1, await service.ScanAsync());
            Assert.DoesNotContain(repository.Tracks, t => t.FileName == "b.mp3");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_自仓储恢复候选池而不触碰磁盘()
    {
        var repository = new StubMusicTrackRepository();
        await repository.ReplaceAllAsync(
            [new MusicTrack { Path = "memory.mp3", FileName = "memory.mp3" }]);

        var service = new MusicLibraryService(repository, new StubSettingsService([]));
        await service.LoadAsync();

        Assert.Single(service.TrackPaths);
        Assert.Equal("memory.mp3", service.TrackPaths[0]);
    }

    [Fact]
    public void TakeRandomTrack_库为空_返回空()
    {
        var service = new MusicLibraryService(new StubMusicTrackRepository(), new StubSettingsService([]));

        Assert.Null(service.TakeRandomTrack());
    }

    [Fact]
    public async Task TakeRandomTrack_仅一首_始终返回该首()
    {
        var root = CreateTempDirectory();

        try
        {
            CreateFile(root, "only.mp3");
            var service = CreateService(new StubMusicTrackRepository(), root);

            await service.ScanAsync();

            Assert.Equal(Path.Combine(root, "only.mp3"), service.TakeRandomTrack());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TakeRandomTrack_多首_连续两次不重复()
    {
        var root = CreateTempDirectory();

        try
        {
            for (var i = 0; i < 8; i++)
            {
                CreateFile(root, $"track{i}.mp3");
            }

            var service = CreateService(new StubMusicTrackRepository(), root);
            await service.ScanAsync();

            var first = service.TakeRandomTrack();
            var second = service.TakeRandomTrack();

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MusicLibraryService CreateService(IMusicTrackRepository repository, params string[] roots) =>
        new(repository, new StubSettingsService(roots));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "pixbian-music-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateFile(string directory, string fileName) =>
        File.WriteAllText(Path.Combine(directory, fileName), "pixbian");

    /// <summary>内存版音乐曲目仓储，用于验证扫描结果的落库口径。</summary>
    private sealed class StubMusicTrackRepository : IMusicTrackRepository
    {
        public List<MusicTrack> Tracks { get; private set; } = [];

        public Task<IReadOnlyList<MusicTrack>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicTrack>>(Tracks);

        public Task ReplaceAllAsync(
            IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken = default)
        {
            Tracks = [.. tracks];
            return Task.CompletedTask;
        }
    }

    /// <summary>固定音乐库目录的设置服务桩件。</summary>
    private sealed class StubSettingsService : ISettingsService
    {
        public StubSettingsService(IReadOnlyList<string> musicPaths) =>
            Current = new AppSettings { MusicLibraryPaths = musicPaths };

        public AppSettings Current { get; private set; }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }
}
