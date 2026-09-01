/**
 * 媒体元数据探测抽象（M3）。
 * 职责：为后台回填声明「只读文件头取尺寸与时长」的契约，使领域层不依赖任何平台解码 API。
 * 复用约定：实现位于 Pixbian（UI 层），依赖 WIC 与 Windows 存储 API，经依赖注入供回填服务使用；
 *          依赖方向严格为 Pixbian → Core，本文件不得引用下层类型。
 * 关键约束：探测只读文件头、绝不解码像素——这是它远快于缩略图解码的唯一原因，实现不得违反；
 *          失败一律返回 null，由调用方置为 Failed 状态，绝不允许把异常抛到调用方。
 */

using Pixbian.Core.Models;

namespace Pixbian.Core.Abstractions;

/// <summary>媒体元数据探测服务。</summary>
public interface IMediaMetadataProbe
{
    /// <summary>读取文件的显示尺寸与时长；失败时返回 null。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="isVideo">是否为视频；决定走图片还是视频的读取路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MediaMetadataProbeResult?> ProbeAsync(
        string path,
        bool isVideo,
        CancellationToken cancellationToken = default);
}
