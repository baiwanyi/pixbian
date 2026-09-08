/**
 * 临时计时日志（P2 验收专用，定位等高/方形视图切换耗时分布后整体删除）。
 * 职责：把目录切换各阶段的耗时追加写入用户日志目录，供离线对比两视图差异。
 * 复用约定：路径取自 AppPaths.LogDirectory；IO 失败静默，绝不影响主流程。
 * 关键约束：本类为临时设施，定位完成后必须连同全部调用一并删除，不得随版本发布。
 */

using System.IO;
using Pixbian.Core.Utilities;

namespace Pixbian.Services;

/// <summary>临时计时日志。</summary>
internal static class TempTiming
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(AppPaths.LogDirectory, "timing.log");

    /// <summary>追加一行计时数据；IO 异常一律静默忽略。</summary>
    public static void Log(string line)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:HH:mm:ss.fff}|{line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 计时日志不可写时放弃记录，不得因此中断调用方。
            }
        }
    }
}
