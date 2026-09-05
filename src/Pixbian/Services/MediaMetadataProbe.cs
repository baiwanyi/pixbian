/**
 * 媒体元数据探测实现（M3）。
 * 职责：实现领域层声明的探测契约，为后台回填提供尺寸与时长。
 * 复用约定：文件头读取统一委托 MediaDimensionReader，与缩略图服务的按需探测共用同一份实现；
 *          媒体类型由调用方按索引条目传入，不在本类重复判定扩展名，避免第二份格式白名单。
 * 关键约束：探测属后台任务，一律在线程池执行且不触碰任何 DependencyObject；
 *          文件类失败（丢失、被占用、格式不受支持）返回 null，由回填服务落为失败状态，绝不重试；
 *          但两类异常**会**抛给调用方，不属探测失败：路径为空白时的 ArgumentException
 *          （参数校验在 try 之前，属调用方错误），以及取消时的 OperationCanceledException。
 */

using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Windows.Storage;

namespace Pixbian.Services;

/// <summary>基于系统文件属性的元数据探测器。</summary>
internal sealed class MediaMetadataProbe : IMediaMetadataProbe
{
    /// <inheritdoc />
    public async Task<MediaMetadataProbeResult?> ProbeAsync(
        string path,
        bool isVideo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var size = await MediaDimensionReader.ReadAsync(file, isVideo);

            return size is null
                ? null
                : new MediaMetadataProbeResult(size.Value.Width, size.Value.Height, size.Value.DurationMs);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            // 文件被移动、占用或格式不受支持时返回空，由回填服务置为失败状态。
            return null;
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
