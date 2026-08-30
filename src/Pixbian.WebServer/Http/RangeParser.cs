/**
 * HTTP Range 头解析器（M7）。
 * 职责：把 Range 请求头解析为字节区间，供流式播放返回 206 响应。
 * 复用约定：支持 bytes=start-end、bytes=start-（到结尾）、bytes=-suffix（末尾 N 字节）三种形式；
 *          多区间请求直接拒绝（返回 false），本服务按单区间实现以控制复杂度。
 * 关键约束：start > end、start >= 文件长度、suffix 为 0 均视为不可满足；
 *          解析必须防注入——只接受 ASCII 数字与连字符，其他字符一律失败，
 *          否则畸形头部可能绕过长度校验导致越界读取。
 */

namespace Pixbian.WebServer.Http;

/// <summary>Range 头解析器。</summary>
public static class RangeParser
{
    private const string UnitPrefix = "bytes=";

    /// <summary>尝试解析 Range 头。</summary>
    /// <param name="header">请求头原始值，如 "bytes=0-1023"。</param>
    /// <param name="totalLength">资源总长度（字节）。</param>
    /// <param name="start">解析出的起始偏移（含）。</param>
    /// <param name="end">解析出的结束偏移（含），已钳制到资源末尾。</param>
    /// <returns>可满足的合法区间返回 true；格式非法或区间不可满足返回 false。</returns>
    public static bool TryParse(string header, long totalLength, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (totalLength <= 0
            || !header.StartsWith(UnitPrefix, StringComparison.Ordinal)
            || header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var spec = header[UnitPrefix.Length..].Trim();

        if (spec.Length == 0 || !IsAsciiNumeric(spec))
        {
            return false;
        }

        var separatorIndex = spec.IndexOf('-', StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            return false;
        }

        var startText = spec[..separatorIndex];
        var endText = spec[(separatorIndex + 1)..];

        // 形如 bytes=-500：取末尾 500 字节。
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return false;
            }

            if (suffix > totalLength)
            {
                suffix = totalLength;
            }

            start = totalLength - suffix;
            end = totalLength - 1;
            return true;
        }

        if (!long.TryParse(startText, out start) || start < 0 || start >= totalLength)
        {
            return false;
        }

        // 形如 bytes=500-：从 500 到结尾。
        if (endText.Length == 0)
        {
            end = totalLength - 1;
            return true;
        }

        if (!long.TryParse(endText, out end) || end < start)
        {
            return false;
        }

        if (end >= totalLength)
        {
            end = totalLength - 1;
        }

        return true;
    }

    /// <summary>校验字符串仅含 ASCII 数字与连字符。</summary>
    private static bool IsAsciiNumeric(string value)
    {
        foreach (var ch in value)
        {
            if (ch is not ((>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
