/**
 * HTTP 请求解析器（M7）。
 * 职责：把套接字读到的字节流解析为 HttpRequest，并对畸形输入做安全拒绝。
 * 复用约定：只支持 HTTP/1.1 与 Content-Length 定长体（不支持 chunked）——
 *          局域网浏览场景下浏览器均按定长提交表单，不支持分块可显著缩小攻击面。
 * 关键约束：请求行与头部的总长度设有上限（防止超大头部耗尽内存，CWE-400）；
 *          请求体大小同样设有上限（防止大包攻击）；解析失败抛 BadRequestException，
 *          由连接处理层转为 400 响应，绝不向客户端泄露内部细节；
 *          路径必须以 / 开头，拒绝绝对 URI 形式（防代理转发滥用）。
 */

using System.Globalization;
using System.Text;

namespace Pixbian.WebServer.Http;

/// <summary>请求不合法时抛出的异常；消息不得回传给客户端。</summary>
public sealed class BadRequestException : Exception
{
    /// <summary>初始化解析异常。</summary>
    /// <param name="message">仅用于服务端日志的原因描述。</param>
    public BadRequestException(string message) : base(message)
    {
    }
}

/// <summary>HTTP 请求解析器。</summary>
public static class HttpRequestParser
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxUriLength = 2048;

    /// <summary>从字节序列解析一个完整请求；数据不足时返回 null（等待更多数据）。</summary>
    /// <param name="buffer">接收缓冲区。</param>
    /// <param name="available">缓冲区内有效字节数。</param>
    public static HttpRequest? Parse(byte[] buffer, int available)
    {
        // 先定位头部结束标记 \r\n\r\n。
        var headerEnd = IndexOfDoubleCrlf(buffer, available);

        if (headerEnd < 0)
        {
            if (available > MaxHeaderBytes)
            {
                throw new BadRequestException("请求头部超过大小上限。");
            }

            return null;
        }

        var headerLength = headerEnd + 4;

        // 找到头部结束标记后同样要检查大小上限——
        // 否则攻击者可用「完整但巨大」的头部绕过上面的未完成检查。
        if (headerLength > MaxHeaderBytes)
        {
            throw new BadRequestException("请求头部超过大小上限。");
        }

        var headerText = Encoding.UTF8.GetString(buffer, 0, headerLength);
        var lines = headerText.Split(["\r\n"], StringSplitOptions.None);

        var requestLine = ParseRequestLine(lines[0]);
        var headers = ParseHeaders(lines.AsSpan(1));

        var contentLength = 0;

        if (headers.TryGetValue("content-length", out var lengthValues)
            && !int.TryParse(lengthValues[0], out contentLength))
        {
            throw new BadRequestException("Content-Length 非法。");
        }

        if (contentLength < 0 || contentLength > MaxBodyBytes)
        {
            throw new BadRequestException("请求体超过大小上限。");
        }

        if (available < headerLength + contentLength)
        {
            return null;
        }

        var body = new byte[contentLength];
        Buffer.BlockCopy(buffer, headerLength, body, 0, contentLength);

        var query = ParseQuery(requestLine.Query);

        return new HttpRequest
        {
            Method = requestLine.Method,
            Path = requestLine.Path,
            Query = query,
            Headers = headers,
            Body = body,
            RemoteIp = string.Empty
        };
    }

    /// <summary>定位 \r\n\r\n 的起始索引；未找到返回 -1。</summary>
    private static int IndexOfDoubleCrlf(byte[] buffer, int available)
    {
        for (var i = 0; i + 3 < available; i++)
        {
            if (buffer[i] == 0x0D && buffer[i + 1] == 0x0A
                && buffer[i + 2] == 0x0D && buffer[i + 3] == 0x0A)
            {
                return i;
            }
        }

        return -1;
    }

    private static (HttpMethodKind Method, string Path, string Query) ParseRequestLine(string line)
    {
        var parts = line.Split(' ');

        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            throw new BadRequestException("请求行格式非法。");
        }

        var method = parts[0].ToUpperInvariant() switch
        {
            "GET" => HttpMethodKind.Get,
            "HEAD" => HttpMethodKind.Head,
            "POST" => HttpMethodKind.Post,
            _ => HttpMethodKind.Unknown
        };

        var target = parts[1];

        if (!target.StartsWith('/'))
        {
            throw new BadRequestException("仅支持相对路径请求。");
        }

        if (target.Length > MaxUriLength)
        {
            throw new BadRequestException("URI 超过长度上限。");
        }

        var separatorIndex = target.IndexOf('?', StringComparison.Ordinal);
        var path = separatorIndex < 0 ? target : target[..separatorIndex];
        var query = separatorIndex < 0 ? string.Empty : target[(separatorIndex + 1)..];

        return (method, Uri.UnescapeDataString(path), query);
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseHeaders(ReadOnlySpan<string> lines)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);

            if (separatorIndex <= 0)
            {
                throw new BadRequestException("头部行格式非法。");
            }

            var name = line[..separatorIndex].Trim().ToLowerInvariant();
            var value = line[(separatorIndex + 1)..].Trim();

            if (headers.TryGetValue(name, out var existing))
            {
                headers[name] = [.. existing, value];
            }
            else
            {
                headers[name] = [value];
            }
        }

        return headers;
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseQuery(string query)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (query.Length == 0)
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(
                separatorIndex < 0 ? pair : pair[..separatorIndex]).ToLowerInvariant();
            var value = separatorIndex < 0
                ? string.Empty
                : Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);

            if (result.TryGetValue(key, out var existing))
            {
                result[key] = [.. existing, value];
            }
            else
            {
                result[key] = [value];
            }
        }

        return result;
    }
}
