/**
 * WebAccessServer 端到端集成测试（M7）。
 * 职责：在固定端口上真实启动服务器，用原始套接字验证健康检查、鉴权拦截与 404 路由。
 * 复用约定：使用内存桩仓储（StubMediaRepository / StubFolderRepository），不依赖真实索引库；
 *          每个用例独占一个端口（18810 起顺延）与实例，测完即释放。
 * 关键约束：必须保留「未授权访问受保护资源返回 401」用例——这是局域网暴露面的第一道闸门。
 */

using System.Globalization;
using System.Net.Sockets;
using System.Text;
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

    /// <summary>创建使用内存桩仓储的服务器实例。</summary>
    private static WebAccessServer CreateServer(string? passwordHash, int port)
    {
        IMediaItemRepository mediaItems = new StubMediaRepository([]);
        ILibraryFolderRepository folders = new StubFolderRepository();

        return new WebAccessServer(mediaItems, folders, passwordHash, port);
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
            Task.FromResult<MediaItem?>(null);

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

    /// <summary>空的扫描源桩仓储。</summary>
    private sealed class StubFolderRepository : ILibraryFolderRepository
    {
        public Task<IReadOnlyList<LibraryFolder>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibraryFolder>>([]);

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
