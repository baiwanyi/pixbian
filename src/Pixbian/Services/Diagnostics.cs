/**
 * 临时诊断日志（第 0 步基线测量专用，取得数据后整体删除）。
 * 职责：把缩略图解码与查看器加载的关键运行数据追加写入用户日志目录，供离线统计。
 * 复用约定：路径统一取自 AppPaths.LogDirectory，与崩溃日志同目录，禁止硬编码目录名。
 * 关键约束：本类为一次性埋点设施，不得随正式版本发布——统计取得基线后必须连同全部
 *           埋点调用一并删除；写入一律加锁串行且失败静默，绝不能影响主流程与耗时测量。
 */

using System.IO;
using Pixbian.Core.Utilities;

namespace Pixbian.Services;

/// <summary>临时诊断日志。</summary>
internal static class Diagnostics
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(AppPaths.LogDirectory, "diag.log");

    /// <summary>追加一行诊断数据；IO 异常一律静默忽略。</summary>
    /// <param name="line">以竖线分隔的记录内容。</param>
    public static void Log(string line)
    {
        var entry = $"{DateTimeOffset.Now:HH:mm:ss.fff}|{line}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogPath, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 诊断日志不可写时放弃记录，不得因此中断调用方。
            }
        }
    }
}
