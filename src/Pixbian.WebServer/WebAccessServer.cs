/**
 * 局域网 Web 访问服务器（M7）。
 * 职责：监听 TCP 端口，处理 HTTP 请求，对外提供媒体库的浏览、缩略图与流式播放。
 * 复用约定：基于 TcpListener 自研 HTTP/1.1（免 urlacl、免管理员）；每连接独立任务处理；
 *          路由全部经 RouteAsync 集中分发，新增端点不得绕过统一的鉴权与限流检查。
 * 关键约束：/media/{id} 与 /thumb/{id} 只接受数据库主键，绝不接受客户端传入的路径；
 *          文件访问前必须经 PathGuard 校验其位于已启用的库目录内（纵深防御）；
 *          对外 JSON 一律不包含绝对路径（泄露用户目录结构属隐私问题）；
 *          Range 解析必须防御 start > end 与越界，非法区间返回 416；
 *          限流与鉴权在路由分发前统一执行，端点自身不重复实现。
 */

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;
using Pixbian.WebServer.Http;
using Pixbian.WebServer.Security;

namespace Pixbian.WebServer;

/// <summary>局域网 Web 访问服务器。</summary>
public sealed partial class WebAccessServer : IAsyncDisposable
{
    // 日志统一使用 LoggerMessage 源生成器，避免每次调用解析模板并装箱参数（CA1848）。
    [LoggerMessage(EventId = 7001, Level = LogLevel.Information,
        Message = "Web 服务已启动，监听端口 {Port}。")]
    private static partial void LogServerStarted(ILogger logger, int port);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information,
        Message = "Web 服务已停止。")]
    private static partial void LogServerStopped(ILogger logger);
    private const int ThumbnailMaxEdge = 320;

    private readonly IMediaItemRepository _mediaItems;
    private readonly ILibraryFolderRepository _libraryFolders;
    private readonly AuthService _auth;
    private readonly int _port;
    private readonly ILogger _logger;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private IReadOnlyList<string> _libraryRoots = [];

    /// <summary>初始化 Web 服务器。</summary>
    /// <param name="mediaItems">媒体条目仓储。</param>
    /// <param name="libraryFolders">扫描源仓储。</param>
    /// <param name="storedPasswordHash">已存储的密码哈希；为空表示不启用鉴权。</param>
    /// <param name="port">监听端口。</param>
    /// <param name="logger">日志记录器；为空时使用空实现。</param>
    public WebAccessServer(
        IMediaItemRepository mediaItems,
        ILibraryFolderRepository libraryFolders,
        string? storedPasswordHash,
        int port,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        ArgumentNullException.ThrowIfNull(libraryFolders);

        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口须在 1024–65535 之间。");
        }

        _mediaItems = mediaItems;
        _libraryFolders = libraryFolders;
        _auth = new AuthService(storedPasswordHash);
        _port = port;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>是否已启用密码保护。</summary>
    public bool IsProtected => _auth.IsProtected;

    /// <summary>当前监听的端口。</summary>
    public int Port => _port;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>可访问的本机地址列表。</summary>
    public IReadOnlyList<string> ActiveUrls { get; private set; } = [];

    /// <summary>启动监听。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_cts is not null, this);

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _libraryRoots = [.. _libraryFolders.GetAllAsync(cancellationToken).ConfigureAwait(false)
            .GetAwaiter().GetResult()
            .Where(f => f.IsEnabled)
            .Select(f => f.Path)];

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        IsRunning = true;
        ActiveUrls = [.. EnumerateLocalUrls(_port)];

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);

        LogServerStarted(_logger, _port);
        return Task.CompletedTask;
    }

    /// <summary>停止监听并断开全部连接。</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _listener?.Stop();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止导致的取消属于预期行为。
            }
        }

        LogServerStopped(_logger);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    /// <summary>枚举本机可访问的 URL。</summary>
    private static List<string> EnumerateLocalUrls(int port)
    {
        var urls = new List<string>();

        foreach (var address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.Loopback.Equals(address))
            {
                urls.Add($"http://{address}:{port}/");
            }
        }

        if (urls.Count == 0)
        {
            urls.Add($"http://localhost:{port}/");
        }

        return urls;
    }

    /// <summary>接受连接的主循环。</summary>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener!;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // 监听器已停止或套接字异常，退出循环。
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>处理单个连接。</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 10_000;
            client.SendTimeout = 30_000;

            var stream = client.GetStream();
            var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();

            // 请求头 + 请求体合计的读取上限；超出即拒绝。
            var buffer = new byte[80 * 1024];
            var total = 0;
            HttpRequest? request = null;

            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;

                try
                {
                    request = HttpRequestParser.Parse(buffer, total);
                }
                catch (BadRequestException)
                {
                    await WriteResponseAsync(stream, HttpResponse.Text(400, "Bad Request"), false)
                        .ConfigureAwait(false);
                    return;
                }

                if (request is not null)
                {
                    request = request with { RemoteIp = remoteIp };
                    break;
                }
            }

            if (request is null)
            {
                return;
            }

            var isHead = request.Method == HttpMethodKind.Head;
            var response = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);

            await WriteResponseAsync(stream, response, isHead).ConfigureAwait(false);
        }
    }

    /// <summary>路由分发：限流与鉴权在此统一执行。</summary>
    private async Task<HttpResponse> HandleRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // 健康检查不鉴权不限流，供网络探测使用。
        if (request.Path.Equals("/api/health", StringComparison.Ordinal))
        {
            return HttpResponse.Json(200, """{"status":"ok"}""");
        }

        if (request.RemoteIp.Length > 0 && _auth.IsRateLimited(request.RemoteIp))
        {
            return HttpResponse.Json(429, """{"error":"请求过于频繁，请稍后再试。"}""");
        }

        if (request.Path.Equals("/api/login", StringComparison.Ordinal))
        {
            return await HandleLoginAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = ExtractToken(request);

        if (!_auth.IsAuthorized(token))
        {
            return HttpResponse.Json(401, """{"error":"未登录或会话已过期。"}""");
        }

        return request.Path switch
        {
            "/" or "/index.html" => WebAssets.Index(),
            "/app.css" => WebAssets.Stylesheet(),
            "/app.js" => WebAssets.Script(),
            _ => await HandleApiAsync(request, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>处理登录。</summary>
    private Task<HttpResponse> HandleLoginAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Method != HttpMethodKind.Post)
        {
            return Task.FromResult(HttpResponse.Json(405, """{"error":"仅支持 POST。"}"""));
        }

        string? password = null;

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            password = document.RootElement.GetProperty("password").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return Task.FromResult(HttpResponse.Json(400, """{"error":"请求格式不正确。"}"""));
        }

        var token = _auth.TryLogin(password, request.RemoteIp);

        if (token is null)
        {
            return Task.FromResult(HttpResponse.Json(401, """{"error":"密码错误或已被临时锁定。"}"""));
        }

        var response = HttpResponse.Json(200, """{"ok":true}""");
        response.Headers["Set-Cookie"] =
            $"pa_token={token}; HttpOnly; SameSite=Lax; Path=/; Max-Age=28800";

        return Task.FromResult(response);
    }

    /// <summary>处理媒体与缩略图路由。</summary>
    private async Task<HttpResponse> HandleApiAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var segments = request.Path.TrimStart('/').Split('/');

        if (request.Method is not (HttpMethodKind.Get or HttpMethodKind.Head))
        {
            // 本服务为只读，不接受任何写请求。
            return HttpResponse.Json(405, """{"error":"只读模式，不接受写操作。"}""");
        }

        // 用显式比较而非列表模式，保持解析行为可预测。
        if (segments.Length == 2
            && segments[0].Equals("api", StringComparison.Ordinal)
            && segments[1].Equals("items", StringComparison.Ordinal))
        {
            return await HandleItemsAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (segments.Length == 3 && segments[0].Equals("api", StringComparison.Ordinal)
            && segments[1].Equals("items", StringComparison.Ordinal))
        {
            return await HandleItemDetailAsync(segments[2], cancellationToken).ConfigureAwait(false);
        }

        if (segments.Length == 2 && segments[0].Equals("thumb", StringComparison.Ordinal))
        {
            return await HandleThumbnailAsync(segments[1], cancellationToken).ConfigureAwait(false);
        }

        if (segments.Length == 2 && segments[0].Equals("media", StringComparison.Ordinal))
        {
            return await HandleMediaAsync(request, segments[1], cancellationToken).ConfigureAwait(false);
        }

        return HttpResponse.Json(404, """{"error":"未找到。"}""");
    }

    /// <summary>条目列表（分页 + 筛选）。</summary>
    private async Task<HttpResponse> HandleItemsAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var query = new MediaQuery
        {
            Kind = ParseKind(request.QueryValue("kind")),
            SearchText = request.QueryValue("q"),
            Skip = ParseInt(request.QueryValue("offset")) ?? 0,
            Take = Math.Clamp(ParseInt(request.QueryValue("limit")) ?? 60, 1, 200)
        };

        var items = await _mediaItems.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        var payload = new
        {
            hasMore = items.Count == query.Take,
            items = items.Select(i => new
            {
                id = i.Id,
                fileName = i.FileName,
                kind = i.Kind == MediaKind.Video ? "video" : "image",
                width = i.Width,
                height = i.Height,
                durationMs = i.DurationMs,
                isFavorite = i.IsFavorite,
                takenUtc = i.TakenUtc
            })
        };

        return HttpResponse.Json(
            200,
            JsonSerializer.Serialize(payload, WebAssets.JsonOptions));
    }

    /// <summary>单条条目详情。</summary>
    private async Task<HttpResponse> HandleItemDetailAsync(
        string idText,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(idText, CultureInfo.InvariantCulture, out var id))
        {
            return HttpResponse.Json(400, """{"error":"非法的条目编号。"}""");
        }

        var item = await _mediaItems.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            return HttpResponse.Json(404, """{"error":"条目不存在。"}""");
        }

        var payload = new
        {
            id = item.Id,
            fileName = item.FileName,
            kind = item.Kind == MediaKind.Video ? "video" : "image",
            width = item.Width,
            height = item.Height,
            fileSize = item.FileSize,
            durationMs = item.DurationMs,
            isFavorite = item.IsFavorite,
            takenUtc = item.TakenUtc,
            modifiedUtc = item.ModifiedUtc
        };

        return HttpResponse.Json(200, JsonSerializer.Serialize(payload, WebAssets.JsonOptions));
    }

    /// <summary>缩略图（仅图片；视频由前端直接用 video 元素）。</summary>
    private async Task<HttpResponse> HandleThumbnailAsync(
        string idText,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(idText, CultureInfo.InvariantCulture, out var id))
        {
            return HttpResponse.Text(400, "Bad Request");
        }

        var item = await _mediaItems.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (item is null
            || item.Kind != MediaKind.Image
            || !IsPathAllowed(item.Path))
        {
            return HttpResponse.Text(404, "Not Found");
        }

        try
        {
            var bytes = await Task.Run(() => EncodeThumbnail(item.Path), cancellationToken)
                .ConfigureAwait(false);

            return new HttpResponse
            {
                StatusCode = 200,
                ContentType = MimeTypes.FromExtension(".jpg"),
                Body = bytes
            };
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or SixLabors.ImageSharp.UnknownImageFormatException
                                      or NotSupportedException)
        {
            return HttpResponse.Text(404, "Not Found");
        }
    }

    /// <summary>原图或视频流，支持 Range 请求（视频拖动进度条依赖）。</summary>
    private async Task<HttpResponse> HandleMediaAsync(
        HttpRequest request,
        string idText,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(idText, CultureInfo.InvariantCulture, out var id))
        {
            return HttpResponse.Text(400, "Bad Request");
        }

        var item = await _mediaItems.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (item is null || !IsPathAllowed(item.Path))
        {
            return HttpResponse.Text(404, "Not Found");
        }

        var fileInfo = new FileInfo(item.Path);

        if (!fileInfo.Exists)
        {
            return HttpResponse.Text(404, "Not Found");
        }

        var contentType = MimeTypes.FromExtension(Path.GetExtension(item.Path));
        var rangeHeader = request.Header("range");

        if (rangeHeader.Length == 0)
        {
            // 无 Range：整文件返回。
            var full = await File.ReadAllBytesAsync(item.Path, cancellationToken).ConfigureAwait(false);

            return new HttpResponse
            {
                StatusCode = 200,
                ContentType = contentType,
                Body = full,
                Headers =
                {
                    ["Accept-Ranges"] = "bytes"
                }
            };
        }

        if (!RangeParser.TryParse(rangeHeader, fileInfo.Length, out var start, out var end))
        {
            var notSatisfiable = HttpResponse.Text(416, "Range Not Satisfiable");
            notSatisfiable.Headers["Content-Range"] =
                string.Create(CultureInfo.InvariantCulture, $"bytes */{fileInfo.Length}");
            return notSatisfiable;
        }

        var length = end - start + 1;
        var segment = new byte[length];

        await using (var stream = fileInfo.OpenRead())
        {
            stream.Seek(start, SeekOrigin.Begin);

            var read = 0;

            while (read < length)
            {
                var chunk = await stream.ReadAsync(
                    segment.AsMemory(read, (int)length - read),
                    cancellationToken).ConfigureAwait(false);

                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }
        }

        var partial = new HttpResponse
        {
            StatusCode = 206,
            ContentType = contentType,
            Body = segment,
            Headers =
            {
                ["Accept-Ranges"] = "bytes",
                ["Content-Range"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"bytes {start}-{end}/{fileInfo.Length}")
            }
        };

        return partial;
    }

    /// <summary>用 ImageSharp 生成最长边不超过上限的 JPEG 缩略图。</summary>
    private static byte[] EncodeThumbnail(string path)
    {
        using var image = SixLabors.ImageSharp.Image.Load(path);

        var scale = Math.Min(
            (float)ThumbnailMaxEdge / image.Width,
            (float)ThumbnailMaxEdge / image.Height);

        if (scale < 1f)
        {
            image.Mutate(context => context.Resize(
                (int)(image.Width * scale),
                (int)(image.Height * scale)));
        }

        using var output = new MemoryStream();
        image.SaveAsJpeg(output);
        return output.ToArray();
    }

    /// <summary>校验文件路径位于已启用的库目录内（纵深防御）。</summary>
    private bool IsPathAllowed(string path)
    {
        foreach (var root in _libraryRoots)
        {
            if (PathGuard.IsInside(root, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>从 Cookie 头提取会话令牌。</summary>
    private static string? ExtractToken(HttpRequest request)
    {
        var cookieHeader = request.Header("cookie");

        foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);

            if (separatorIndex > 0
                && pair[..separatorIndex].Trim().Equals("pa_token", StringComparison.Ordinal))
            {
                return pair[(separatorIndex + 1)..].Trim();
            }
        }

        return request.QueryValue("token");
    }

    private static MediaKind? ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        "image" => MediaKind.Image,
        "video" => MediaKind.Video,
        _ => null
    };

    private static int? ParseInt(string? value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>把响应写入套接字。</summary>
    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpResponse response,
        bool isHead)
    {
        response.ApplySecurityHeaders();
        response.Headers["Connection"] = "close";

        var bytes = response.ToBytes(isHead);
        await stream.WriteAsync(bytes, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
