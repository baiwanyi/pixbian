/**
 * HTTP 协议基础类型（M7）。
 * 职责：定义请求、响应与工具方法的最小表示，供自研 HTTP 服务器的解析与应答使用。
 * 复用约定：头部值允许同键多值（如多个 Set-Cookie），故一律用 IReadOnlyList&lt;string&gt; 存储；
 *          MIME 映射覆盖本项目支持的媒体格式，未登记的类型按 application/octet-stream 处理。
 * 关键约束：响应行与头部统一 UTF-8 + CRLF；禁止把用户输入直接拼进状态行或头部，
 *          否则会出现响应拆分（CWE-113），故头部值在写入前必须过滤 CR/LF。
 */

using System.Globalization;
using System.Text;

namespace Pixbian.WebServer.Http;

/// <summary>HTTP 请求方法。</summary>
public enum HttpMethodKind
{
    /// <summary>读取。</summary>
    Get,

    /// <summary>读取头部。</summary>
    Head,

    /// <summary>提交数据。</summary>
    Post,

    /// <summary>不支持的方法。</summary>
    Unknown
}

/// <summary>解析后的 HTTP 请求。</summary>
public sealed record HttpRequest
{
    /// <summary>请求方法。</summary>
    public required HttpMethodKind Method { get; init; }

    /// <summary>请求路径（不含查询串）。</summary>
    public required string Path { get; init; }

    /// <summary>查询参数；键为小写，值为数组以支持重复键。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; init; }

    /// <summary>请求头部；键为小写。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; init; }

    /// <summary>请求体。</summary>
    public required byte[] Body { get; init; }

    /// <summary>来源 IP。</summary>
    public required string RemoteIp { get; init; }

    /// <summary>读取首个匹配的头部值；缺失时返回空串。</summary>
    /// <param name="name">头部名称，不区分大小写。</param>
    public string Header(string name)
    {
        return Headers.TryGetValue(name.ToLowerInvariant(), out var values) && values.Count > 0
            ? values[0]
            : string.Empty;
    }

    /// <summary>读取首个匹配的查询参数；缺失时返回 null。</summary>
    /// <param name="name">参数名称，不区分大小写。</param>
    public string? QueryValue(string name)
    {
        return Query.TryGetValue(name.ToLowerInvariant(), out var values) && values.Count > 0
            ? values[0]
            : null;
    }
}

/// <summary>待发送的 HTTP 响应。</summary>
public sealed class HttpResponse
{
    private const string ServerHeader = "Pixbian/1.0";

    /// <summary>状态码。</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Content-Type；为空时省略该头部。</summary>
    public string? ContentType { get; set; }

    /// <summary>响应体。</summary>
    public byte[] Body { get; set; } = [];

    /// <summary>附加头部。</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>附加标准安全头。</summary>
    /// <remarks>所有响应都必须调用，缺失安全头会被安全自检表判为不合规。</remarks>
    public void ApplySecurityHeaders()
    {
        Headers["X-Content-Type-Options"] = "nosniff";
        Headers["X-Frame-Options"] = "DENY";
        Headers["Referrer-Policy"] = "no-referrer";
        Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; media-src 'self'; script-src 'self'; style-src 'self'";
    }

    /// <summary>写入纯文本正文。</summary>
    /// <param name="statusCode">状态码。</param>
    /// <param name="text">文本内容。</param>
    public static HttpResponse Text(int statusCode, string text) => new()
    {
        StatusCode = statusCode,
        ContentType = "text/plain; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(text)
    };

    /// <summary>写入 JSON 正文。</summary>
    /// <param name="statusCode">状态码。</param>
    /// <param name="json">JSON 文本。</param>
    public static HttpResponse Json(int statusCode, string json) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(json)
    };

    /// <summary>写入 HTML 正文。</summary>
    /// <param name="html">HTML 文本。</param>
    public static HttpResponse Html(string html) => new()
    {
        StatusCode = 200,
        ContentType = "text/html; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(html)
    };

    /// <summary>序列化为线上字节（含头部与正文）。</summary>
    /// <param name="isHeadRequest">HEAD 请求只发头部不发正文。</param>
    public byte[] ToBytes(bool isHeadRequest)
    {
        var builder = new StringBuilder(512);

        builder.Append("HTTP/1.1 ")
            .Append(StatusCode.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(GetReasonPhrase(StatusCode))
            .Append("\r\n");

        if (!string.IsNullOrEmpty(ContentType))
        {
            builder.Append("Content-Type: ").Append(ContentType).Append("\r\n");
        }

        builder.Append("Content-Length: ")
            .Append(Body.Length.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");

        foreach (var header in Headers)
        {
            // 过滤 CR/LF，阻断响应拆分注入。
            var value = header.Value.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);

            builder.Append(header.Key).Append(": ").Append(value).Append("\r\n");
        }

        builder.Append("Server: ").Append(ServerHeader).Append("\r\n\r\n");

        var headBytes = Encoding.UTF8.GetBytes(builder.ToString());

        if (isHeadRequest || Body.Length == 0)
        {
            return headBytes;
        }

        var result = new byte[headBytes.Length + Body.Length];
        Buffer.BlockCopy(headBytes, 0, result, 0, headBytes.Length);
        Buffer.BlockCopy(Body, 0, result, headBytes.Length, Body.Length);

        return result;
    }

    private static string GetReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        206 => "Partial Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "OK"
    };
}

/// <summary>按扩展名推断 MIME 类型。</summary>
public static class MimeTypes
{
    /// <summary>按扩展名获取 MIME 类型；未登记时返回二进制流类型。</summary>
    /// <param name="extension">扩展名（可含点号）。</param>
    public static string FromExtension(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "jpe" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "tif" or "tiff" => "image/tiff",
        "heic" or "heif" => "image/heic",
        "avif" => "image/avif",
        "ico" => "image/x-icon",
        "mp4" or "m4v" => "video/mp4",
        "mov" => "video/quicktime",
        "avi" => "video/x-msvideo",
        "mkv" => "video/x-matroska",
        "webm" => "video/webm",
        "wmv" => "video/x-ms-wmv",
        "ts" => "video/mp2t",
        "3gp" => "video/3gpp",
        "css" => "text/css; charset=utf-8",
        "js" => "text/javascript; charset=utf-8",
        "json" => "application/json; charset=utf-8",
        "html" or "htm" => "text/html; charset=utf-8",
        "svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
}
