/**
 * 应用日志文件写入器。
 * 职责：把运行期事件（崩溃、安全审计、告警）按级别追加写入用户日志目录，并按大小滚动归档。
 * 复用约定：路径统一取自 AppPaths.LogDirectory；级别语义沿用 Microsoft.Extensions.Logging，但本类只依赖 BCL，
 *          使领域层与 Web 服务层也能直接使用，不必引入具体日志实现。
 * 关键约束：本类是「最后一道防线」，任何失败都必须静默——日志写入抛异常会掩盖真正的故障原因；
 *          写入一律加锁串行并即时落盘（崩溃前的最后一条最有价值），不做异步缓冲；
 *          敏感信息必须先经 RedactIp / RedactPath：IP 只留 /24、路径只留文件名，
 *          日志可能随用户反馈外发，全路径与完整 IP 均属隐私泄露；
 *          单文件大小与归档份数必须受限，否则长期运行会写满用户磁盘。
 */

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Pixbian.Core.Utilities;

/// <summary>应用日志写入器。</summary>
public static class AppLog
{
    /// <summary>日志文件名。</summary>
    public const string FileName = "app.log";

    /// <summary>归档份数；滚动到上限后最旧一份被覆盖。</summary>
    private const int MaxArchiveCount = 3;

    private static readonly object Gate = new();

    /// <summary>单文件滚动阈值（字节）；仅建议测试调整，生产保持默认。</summary>
    public static long MaxFileSizeBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>当前日志文件的完整路径。</summary>
    public static string LogFilePath => Path.Combine(AppPaths.LogDirectory, FileName);

    /// <summary>记录一条信息级日志。</summary>
    /// <param name="category">事件类别，用于检索（如 Web、Indexing）。</param>
    /// <param name="message">已脱敏的消息文本。</param>
    public static void Info(string category, string message) => Write("INFO", category, message);

    /// <summary>记录一条警告级日志。</summary>
    /// <param name="category">事件类别。</param>
    /// <param name="message">已脱敏的消息文本。</param>
    public static void Warn(string category, string message) => Write("WARN", category, message);

    /// <summary>记录一条错误级日志（可附带异常）。</summary>
    /// <param name="category">事件类别。</param>
    /// <param name="message">已脱敏的消息文本。</param>
    /// <param name="exception">异常；堆栈可能含用户路径，仅在本地日志保留。</param>
    public static void Error(string category, string message, Exception? exception = null) =>
        Write("ERROR", category, message, exception?.ToString());

    /// <summary>把 IP 脱敏到 /24：日志可能随反馈外发，完整 IP 属可定位信息。</summary>
    /// <param name="ip">原始 IP 文本。</param>
    /// <returns>IPv4 返回 `a.b.c.0/24`，IPv6 截取前段，无法解析时返回 `-`。</returns>
    public static string RedactIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return "-";
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24");
        }

        // IPv6 保留前两段（通常即运营商前缀），其余截断。
        var text = address.ToString();
        var segments = text.Split(':');

        return segments.Length < 2 ? "-" : $"{segments[0]}:{segments[1]}::/32";
    }

    /// <summary>把文件路径脱敏为文件名：全路径会泄露用户的目录结构与盘符布局。</summary>
    /// <param name="path">原始路径。</param>
    /// <returns>文件名；路径为空或非法时返回 `-`。</returns>
    public static string RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "-";
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return "-";
        }
    }

    /// <summary>写入一条日志；目录缺失时自动创建，任何 IO 失败一律静默。</summary>
    /// <param name="level">级别文本。</param>
    /// <param name="category">事件类别。</param>
    /// <param name="message">消息正文；其中的换行会被替换为空格。</param>
    /// <param name="detail">附加详情（如异常堆栈）；保留原始换行，不做清洗。</param>
    /// <remarks>
    /// 消息正文必须清洗 CR/LF：请求路径等外部输入可经百分号编码夹带换行，
    /// 直接写入会伪造出额外的日志行（日志注入），进而污染审计记录的可信度。
    /// </remarks>
    private static void Write(string level, string category, string message, string? detail = null)
    {
        var safeMessage = message
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        var body = detail is null
            ? safeMessage
            : $"{safeMessage}{Environment.NewLine}{detail}";

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.UtcNow:O}][{level}][{category}] {body}{Environment.NewLine}");

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);

                var path = LogFilePath;
                RotateIfNeeded(path);

                File.AppendAllText(path, line);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                // 日志不可写时放弃记录，绝不影响调用方。
            }
        }
    }

    /// <summary>超过阈值时把现有日志依次滚动为 .1 / .2 / .3，最旧一份被覆盖。</summary>
    /// <param name="path">当前日志文件路径。</param>
    /// <remarks>公开以便测试与启动时显式调用；日常由 Write 内部自动触发。</remarks>
    public static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length < MaxFileSizeBytes)
            {
                return;
            }

            for (var index = MaxArchiveCount; index >= 1; index--)
            {
                var source = index == 1 ? path : $"{path}.{index - 1}";
                var target = $"{path}.{index}";

                if (File.Exists(source))
                {
                    File.Move(source, target, overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            // 滚动失败不阻断写入：宁可单文件变大，也不能丢日志。
        }
    }
}
