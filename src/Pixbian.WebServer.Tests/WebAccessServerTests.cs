/**
 * WebAccessServer 端到端集成测试（M7）。
 * 职责：在固定端口上真实启动服务器，用原始套接字验证健康检查、鉴权拦截与 404 路由。
 * 复用约定：使用内存桩仓储（StubMediaRepository / StubFolderRepository），不依赖真实索引库；
 *          每个用例独占一个端口（18810 起顺延）与实例，测完即释放。
 * 关键约束：必须保留「未授权访问受保护资源返回 401」用例——这是局域网暴露面的第一道闸门；
 *          媒体响应必须按字节断言（区间不得越界、整文件不得整读进内存后再输出），
 *          并发用例必须验证慢速连接被超时回收，二者共同构成资源耗尽防护的回归网。
 */

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using SixLabors.ImageSharp;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.WebServer;
using Pixbian.WebServer.Security;
using Xunit;

namespace Pixbian.WebServer.Tests;

/// <summary>WebAccessServer 端到端测试。</summary>
public sealed class WebAccessServerTests
{
    [Fact]
    public async Task StartAsync_健康检查_无需鉴权即可访问()
    {
        await using var server = CreateServer(passwordHash: null, port: 18810);
        await server.StartAsync();

        var (status, _) = await RawRequestAsync(server.Port, "GET /api/health HTTP/1.1");

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task StartAsync_主页_返回HTML()
    {
        await using var server = CreateServer(null, 18811);
        await server.StartAsync();

        var (status, body) = await RawRequestAsync(server.Port, "GET / HTTP/1.1");

        Assert.Equal(200, status);
        Assert.Contains("Pixbian", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_未登录访问条目接口_返回401()
    {
        await using var server = CreateServer(AuthService.HashPassword("secret"), 18812);
        await server.StartAsync();

        var (status, _) = await RawRequestAsync(server.Port, "GET /api/items HTTP/1.1");

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task StartAsync_登录后携带令牌_可访问条目接口()
    {
        await using var server = CreateServer(AuthService.HashPassword("secret"), 18813);
        await server.StartAsync();

        var loginBody = Encoding.UTF8.GetBytes("""{"password":"secret"}""");
        var loginHead = Encoding.UTF8.GetBytes(
            $"POST /api/login HTTP/1.1\r\nContent-Length: {loginBody.Length}\r\n\r\n");

        var loginRequest = new byte[loginHead.Length + loginBody.Length];
        Buffer.BlockCopy(loginHead, 0, loginRequest, 0, loginHead.Length);
        Buffer.BlockCopy(loginBody, 0, loginRequest, loginHead.Length, loginBody.Length);

        var (loginStatus, _, loginHeaders) = await RawRequestBytesWithHeadersAsync(server.Port, loginRequest);

        Assert.Equal(200, loginStatus);

        var cookie = loginHeaders
            .FirstOrDefault(h => h.StartsWith("Set-Cookie:", StringComparison.Ordinal));

        Assert.NotNull(cookie);

        var token = cookie![("Set-Cookie:".Length)..].Trim()
            .Split(';')[0]["pa_token=".Length..];

        var (status, _) = await RawRequestAsync(
            server.Port,
            $"GET /api/items HTTP/1.1\r\nCookie: pa_token={token}");

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task StartAsync_登出后令牌立即失效()
    {
        await using var server = CreateServer(AuthService.HashPassword("secret"), 18825);
        await server.StartAsync();

        var loginBody = Encoding.UTF8.GetBytes("""{"password":"secret"}""");
        var loginHead = Encoding.UTF8.GetBytes(
            $"POST /api/login HTTP/1.1\r\nContent-Length: {loginBody.Length}\r\n\r\n");

        var loginRequest = new byte[loginHead.Length + loginBody.Length];
        Buffer.BlockCopy(loginHead, 0, loginRequest, 0, loginHead.Length);
        Buffer.BlockCopy(loginBody, 0, loginRequest, loginHead.Length, loginBody.Length);

        var (_, _, loginHeaders) = await RawRequestBytesWithHeadersAsync(server.Port, loginRequest);

        var cookie = loginHeaders
            .First(h => h.StartsWith("Set-Cookie:", StringComparison.Ordinal));
        var token = cookie![("Set-Cookie:".Length)..].Trim().Split(';')[0]["pa_token=".Length..];

        // 登出：吊销会话并下发过期 Cookie。
        var (logoutStatus, _) = await RawRequestAsync(
            server.Port, $"POST /api/logout HTTP/1.1\r\nCookie: pa_token={token}");
        Assert.Equal(200, logoutStatus);

        // 旧令牌必须立即失效：登出后旧 Cookie 访问受保护资源返回 401。
        var (status, _) = await RawRequestAsync(
            server.Port, $"GET /api/items HTTP/1.1\r\nCookie: pa_token={token}");
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task StartAsync_未知路径_返回404()
    {
        await using var server = CreateServer(null, 18814);
        await server.StartAsync();

        var (status, _) = await RawRequestAsync(server.Port, "GET /nonexistent HTTP/1.1");

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task StartAsync_畸形请求_返回400且服务器不崩溃()
    {
        await using var server = CreateServer(null, 18815);
        await server.StartAsync();

        var (status, _) = await RawRequestAsync(server.Port, "NOT-HTTP AT ALL\r\n\r\n");

        Assert.Equal(400, status);

        // 服务器在畸形请求后仍应可服务后续请求。
        var (healthStatus, _) = await RawRequestAsync(server.Port, "GET /api/health HTTP/1.1");
        Assert.Equal(200, healthStatus);
    }

    [Fact]
    public async Task StartAsync_响应携带安全头()
    {
        await using var server = CreateServer(null, 18816);
        await server.StartAsync();

        var (_, _, headers) = await RawRequestWithHeadersAsync(server.Port, "GET /api/health HTTP/1.1");

        Assert.Contains(headers, h => h.StartsWith("X-Content-Type-Options:", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.StartsWith("Content-Security-Policy:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_媒体请求_流式返回完整文件()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-media-");

        try
        {
            var content = new byte[64 * 1024];
            Random.Shared.NextBytes(content);

            var path = Path.Combine(directory.FullName, "clip.bin");
            await File.WriteAllBytesAsync(path, content);

            await using var server = CreateServerWithMedia(path, directory.FullName, null, 18821);
            await server.StartAsync();

            var (status, body, headers) = await RawRequestBinaryAsync(
                server.Port,
                "GET /media/1 HTTP/1.1\r\n\r\n");

            Assert.Equal(200, status);
            Assert.Contains(headers, h => h.Equals("Content-Length: 65536", StringComparison.Ordinal));
            Assert.Equal(content, body);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_媒体区间请求_严格按范围截断()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-range-");

        try
        {
            var content = new byte[1024];
            Random.Shared.NextBytes(content);

            var path = Path.Combine(directory.FullName, "clip.bin");
            await File.WriteAllBytesAsync(path, content);

            await using var server = CreateServerWithMedia(path, directory.FullName, null, 18822);
            await server.StartAsync();

            var (status, body, headers) = await RawRequestBinaryAsync(
                server.Port,
                "GET /media/1 HTTP/1.1\r\nRange: bytes=16-31\r\n\r\n");

            Assert.Equal(206, status);

            // 越界写出（多给尾部字节）会让播放器把后续数据当成下一帧，必须严格按 16 字节截断。
            Assert.Equal(16, body.Length);
            Assert.Equal(content[16..32], body);
            Assert.Contains(headers, h => h.StartsWith("Content-Range:", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_缩略图请求_正常尺寸返回图片()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-thumb-");

        try
        {
            var path = Path.Combine(directory.FullName, "tiny.png");

            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(50, 50))
            {
                image.SaveAsPng(path);
            }

            await using var server = CreateServerWithMedia(path, directory.FullName, null, 18823);
            await server.StartAsync();

            var (status, _, headers) = await RawRequestBinaryAsync(server.Port, "GET /thumb/1 HTTP/1.1\r\n\r\n");

            Assert.Equal(200, status);
            Assert.Contains(headers, h => h.StartsWith("Content-Type: image/", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_缩略图请求_源图超解码上限返回404()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-bomb-");

        try
        {
            // 50×50 = 2500 像素；把上限调到 1000 即可模拟「解码炸弹」而不必生成超大图。
            var path = Path.Combine(directory.FullName, "tiny.png");

            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(50, 50))
            {
                image.SaveAsPng(path);
            }

            await using var server = CreateServerWithMedia(path, directory.FullName, null, 18824);
            server.MaxDecodedPixels = 1000;
            await server.StartAsync();

            var (status, _) = await RawRequestAsync(server.Port, "GET /thumb/1 HTTP/1.1");

            Assert.Equal(404, status);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_慢速连接占满并发_超时回收后服务恢复()
    {
        await using var server = CreateServer(null, 18820);
        await server.StartAsync();

        // Slowloris 形态：建立远超并发上限的连接，且一律不发数据。
        var idle = new List<TcpClient>();

        for (var i = 0; i < 40; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            idle.Add(client);
        }

        try
        {
            // 首字节上限（10 秒）过后，全部连接应被回收，服务重新可服务。
            await Task.Delay(TimeSpan.FromSeconds(12));

            var (status, _) = await RawRequestAsync(server.Port, "GET /api/health HTTP/1.1");
            Assert.Equal(200, status);
        }
        finally
        {
            foreach (var client in idle)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>创建使用内存桩仓储的服务器实例。</summary>
    private static WebAccessServer CreateServer(string? passwordHash, int port)
    {
        IMediaItemRepository mediaItems = new StubMediaRepository([]);
        ILibraryFolderRepository folders = new StubFolderRepository();

        return new WebAccessServer(mediaItems, folders, passwordHash, port);
    }

    [Fact]
    public async Task StartAsync_绑定指定地址_可访问地址收敛且服务可用()
    {
        IMediaItemRepository mediaItems = new StubMediaRepository([]);
        ILibraryFolderRepository folders = new StubFolderRepository();
        await using var server = new WebAccessServer(mediaItems, folders, null, 18830, logger: null, bindAddress: "127.0.0.1");

        await server.StartAsync();

        // 绑定具体网卡后只列该地址：其它网卡的请求到不了监听器，列出会造成误导。
        Assert.Equal(new[] { "http://127.0.0.1:18830/" }, server.ActiveUrls);

        var (status, _) = await RawRequestAsync(server.Port, "GET /api/health HTTP/1.1");

        Assert.Equal(200, status);
    }

    [Fact]
    public void 构造_绑定地址格式非法_抛参数异常()
    {
        IMediaItemRepository mediaItems = new StubMediaRepository([]);
        ILibraryFolderRepository folders = new StubFolderRepository();

        // 显式配置不可静默回退到全网卡监听：安全相关的降级必须是显式失败。
        Assert.Throws<ArgumentException>(() =>
            new WebAccessServer(mediaItems, folders, null, 18831, logger: null, bindAddress: "not-an-ip"));
    }

    [Fact]
    public async Task StartAsync_未指定绑定地址_保持全部网卡监听行为()
    {
        await using var server = CreateServer(passwordHash: null, port: 18832);

        await server.StartAsync();

        // 默认（未指定网卡）保持既有行为：列出本机可访问地址，不回退为空或回环。
        Assert.NotEmpty(server.ActiveUrls);
        Assert.All(server.ActiveUrls, url => Assert.StartsWith("http://", url, StringComparison.Ordinal));
    }

    /// <summary>创建含单个媒体条目、且该条目位于已启用扫描源内的服务器实例。</summary>
    private static WebAccessServer CreateServerWithMedia(
        string mediaPath,
        string libraryRoot,
        string? passwordHash,
        int port)
    {
        var item = new MediaItem
        {
            Id = 1,
            Path = mediaPath,
            FileName = Path.GetFileName(mediaPath),
            Directory = libraryRoot,
            Kind = MediaKind.Image,
            FileSize = new FileInfo(mediaPath).Length
        };

        IMediaItemRepository mediaItems = new StubMediaRepository([item]);
        ILibraryFolderRepository folders = new StubFolderRepository(libraryRoot);

        return new WebAccessServer(mediaItems, folders, passwordHash, port);
    }

    /// <summary>发送原始请求并按字节返回状态码、正文与头部行，供二进制响应断言使用。</summary>
    private static async Task<(int Status, byte[] Body, List<string> Headers)> RawRequestBinaryAsync(
        int port,
        string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);

        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(rawRequest));
        await stream.FlushAsync();

        var buffer = new byte[256 * 1024];
        var total = 0;
        int read;

        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total))) > 0)
        {
            total += read;
        }

        var separator = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);

        var headerText = Encoding.UTF8.GetString(buffer, 0, separator);
        var lines = headerText.Split(["\r\n"], StringSplitOptions.None);

        return (
            int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture),
            buffer[(separator + 4)..total],
            lines.Skip(1).ToList());
    }

    /// <summary>空的媒体条目桩仓储。</summary>
    private sealed class StubMediaRepository : IMediaItemRepository
    {
        private readonly List<MediaItem> _items;

        public StubMediaRepository(IReadOnlyList<MediaItem> items) => _items = [.. items];

        public Task UpsertBatchAsync(
            IReadOnlyList<MediaItem> items, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetPathsUnderDirectoryAsync(
            string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteByPathsAsync(
            IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByIdsAsync(
            IReadOnlyList<long> ids, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MediaItem>> QueryAsync(
            MediaQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task SetFavoriteAsync(
            IReadOnlyList<long> ids, bool isFavorite, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountAsync(MediaKind? kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> CountByQueryAsync(MediaQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<MediaItem?> GetAtOffsetAsync(
            MediaKind? kind, int offset, CancellationToken cancellationToken = default) =>
            Task.FromResult<MediaItem?>(null);

        public Task<MediaItem?> GetByIdAsync(
            long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<MediaItem>> GetMetadataPendingAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task UpdateMetadataBatchAsync(
            IReadOnlyList<MediaMetadataUpdate> updates, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>发送原始请求字节并返回状态码、正文与头部行。</summary>
    private static async Task<(int Status, string Body, List<string> Headers)> RawRequestBytesWithHeadersAsync(
        int port,
        byte[] bytes)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);

        var stream = client.GetStream();
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var buffer = new byte[64 * 1024];
        var total = 0;
        int read;

        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total))) > 0)
        {
            total += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerPart = text[..headerEnd];
        var body = text[(headerEnd + 4)..];
        var lines = headerPart.Split(["\r\n"], StringSplitOptions.None);

        return (int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), body, lines.Skip(1).ToList());
    }

    /// <summary>发送原始 HTTP 请求并返回状态码与正文。</summary>
    private static async Task<(int Status, string Body)> RawRequestAsync(int port, string requestLine)
    {
        var (status, body, _) = await RawRequestWithHeadersAsync(
            port,
            $"{requestLine}\r\nHost: test\r\n\r\n");

        return (status, body);
    }

    /// <summary>发送原始请求（含体内部分）并返回状态码、正文与头部行。</summary>
    private static async Task<(int Status, string Body, List<string> Headers)> RawRequestWithHeadersAsync(
        int port,
        string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);

        var raw = rawRequest.Contains("\r\n", StringComparison.Ordinal)
            ? rawRequest
            : $"{rawRequest}\r\nHost: test\r\n\r\n";

        var bytes = Encoding.UTF8.GetBytes(raw);
        var stream = client.GetStream();
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var buffer = new byte[64 * 1024];
        var total = 0;
        int read;

        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total))) > 0)
        {
            total += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerPart = text[..headerEnd];
        var body = text[(headerEnd + 4)..];
        var lines = headerPart.Split(["\r\n"], StringSplitOptions.None);

        return (int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), body, lines.Skip(1).ToList());
    }

    /// <summary>扫描源桩仓储；传入的路径即为已启用的库目录，用于路径归属校验。</summary>
    private sealed class StubFolderRepository : ILibraryFolderRepository
    {
        private readonly List<LibraryFolder> _folders;

        public StubFolderRepository(params string[] paths) =>
            _folders = [.. paths.Select((path, index) => new LibraryFolder
            {
                Id = index + 1,
                Path = path,
                DisplayName = path,
                IsEnabled = true
            })];

        public Task<IReadOnlyList<LibraryFolder>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibraryFolder>>(_folders);

        public Task<LibraryFolder> AddAsync(
            string path, string? displayName = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LibraryFolder { Id = 1, Path = path, DisplayName = path });

        public Task RemoveAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateLastScanAsync(
            long id, DateTimeOffset scannedUtc, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
