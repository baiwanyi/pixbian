/**
 * 视频元数据读取服务（M4）。
 * 职责：读取视频文件的时长、分辨率、编码、码率与音频参数。
 * 复用约定：基础属性取自 VideoProperties，编码格式取自 MediaClip 的编码属性；
 *          两者互补——VideoProperties 拿不到编码名，MediaClip 拿不到部分容器信息。
 * 关键约束：读取失败（文件损坏、编码不支持、文件被占用）一律返回 null 而非抛出，
 *          否则一个坏文件会让整个索引任务中断；
 *          MediaClip 持有非托管资源，用完必须释放；
 *          编码子类型返回的是 GUID 或 "H264" 这类标记，需映射为人类可读名称。
 */

using System.Globalization;
using System.IO;
using Pixbian.Media.Models;

namespace Pixbian.Media.Services;

/// <summary>视频元数据读取服务。</summary>
public interface IVideoMetadataReader
{
    /// <summary>读取视频元数据；失败时返回 null。</summary>
    /// <param name="path">视频文件完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<VideoMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>基于 Windows 系统 API 的视频元数据读取服务。</summary>
public sealed class VideoMetadataReader : IVideoMetadataReader
{
    /// <inheritdoc />
    public async Task<VideoMetadata?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.GetVideoPropertiesAsync();

            cancellationToken.ThrowIfCancellationRequested();

            string? videoCodec = null;
            string? audioCodec = null;
            double? frameRate = null;
            int? channels = null;
            int? sampleRate = null;

            // MediaClip 可解析编码属性，但并非所有容器都支持，失败时降级为仅基础属性。
            // MediaClip 是 WinRT 对象，由运行时负责回收，不存在可显式调用的 Dispose。
            try
            {
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);

                var videoEncoding = clip.GetVideoEncodingProperties();
                videoCodec = NormalizeCodec(videoEncoding.Subtype);
                frameRate = ParseFrameRate(videoEncoding.FrameRate);

                // MediaClip 本身不提供音频编码属性，需经首个内嵌音轨获取。
                if (clip.EmbeddedAudioTracks.Count > 0)
                {
                    var audioEncoding = clip.EmbeddedAudioTracks[0].GetAudioEncodingProperties();
                    audioCodec = NormalizeCodec(audioEncoding.Subtype);
                    channels = (int)audioEncoding.ChannelCount;
                    sampleRate = audioEncoding.SampleRate > 0 ? (int)audioEncoding.SampleRate : null;
                }
            }
            catch (Exception ex) when (ex is NotSupportedException
                                          or InvalidOperationException
                                          or IOException
                                          or ArgumentException)
            {
                // 编码信息不可用时保留基础属性，不整体失败。
            }

            if (properties.Width == 0 && properties.Height == 0 && properties.Duration == TimeSpan.Zero)
            {
                return null;
            }

            return new VideoMetadata
            {
                Duration = properties.Duration > TimeSpan.Zero ? properties.Duration : null,
                Width = properties.Width > 0 ? (int)properties.Width : null,
                Height = properties.Height > 0 ? (int)properties.Height : null,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Bitrate = properties.Bitrate > 0 ? properties.Bitrate : null,
                FrameRate = frameRate,
                AudioChannels = channels,
                AudioSampleRate = sampleRate,
                ContainerFormat = NormalizeCodec(Path.GetExtension(path).TrimStart('.'))
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                      or UnauthorizedAccessException
                                      or IOException
                                      or ArgumentException
                                      or NotSupportedException
                                      or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>把编码子类型标记规范化为小写名称；无法识别时原样返回。</summary>
    private static string? NormalizeCodec(string? subtype)
    {
        if (string.IsNullOrWhiteSpace(subtype))
        {
            return null;
        }

        var trimmed = subtype.Trim();

        // 部分容器返回的是 GUID 形式，无法直接展示，直接判定为未知。
        if (trimmed.StartsWith('{'))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>解析帧率；分子或分母为 0 时表示不可用。</summary>
    private static double? ParseFrameRate(Windows.Media.MediaProperties.MediaRatio frameRate)
    {
        if (frameRate.Denominator == 0)
        {
            return null;
        }

        var value = (double)frameRate.Numerator / frameRate.Denominator;
        return value > 0 ? value : null;
    }
}
