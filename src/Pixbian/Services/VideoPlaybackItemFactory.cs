/**
 * 视频播放项工厂。
 * 职责：为视频文件创建可直接赋给 MediaPlayer 的播放项，优先 FFmpeg 解码、失败时回退系统解码器。
 * 复用约定：解码后端按编码选择——AV1 在本机（UHD 630 无硬解）下系统解码器虽能播但走自身软解，
 *           效率远低于 FFmpeg 的 dav1d 多线程软解（实测 4K AV1 长期接近 100% CPU），
 *           故对 AV1 显式强制 FFmpeg 软解，其余编码保持自动以不改变既有硬解行为；
 *           播放器页与短片页共用本工厂，避免两处解码策略各自漂移。
 * 关键约束：FFmpegMediaSource 必须由返回的播放项强引用持有——被 GC 回收会直接中断播放（官方明确警告）；
 *           切换视频时必须 Dispose 上一个播放项，否则 FFmpeg 的解码上下文与文件句柄滞留到进程回收；
 *           本工厂位于 UI 项目而非 Pixbian.Media：FFmpegInteropX 只被本项目引用，Media 层刻意不引入。
 */

using System.IO;
using FFmpegInteropX;
using Pixbian.Media.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Pixbian.Services;

/// <summary>视频播放项：把播放项与其 FFmpeg 解码源绑在一起，保证二者同时释放。</summary>
public sealed class VideoPlaybackItem : IDisposable
{
    private FFmpegMediaSource? _ffmpegSource;
    private bool _disposed;

    /// <summary>初始化播放项。</summary>
    /// <param name="item">可直接赋给 MediaPlayer 的播放项。</param>
    /// <param name="ffmpegSource">FFmpeg 解码源；回退系统解码器时为 null。</param>
    internal VideoPlaybackItem(MediaPlaybackItem item, FFmpegMediaSource? ffmpegSource)
    {
        Item = item;
        _ffmpegSource = ffmpegSource;
    }

    /// <summary>可直接赋给 <see cref="MediaPlayer.Source"/> 的播放项。</summary>
    public MediaPlaybackItem Item { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        // 释放被设计为可异步执行（切换时交线程池清理解码上下文），可能与异常路径的
        // 同步释放交汇——必须幂等，二次调用直接返回。
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_ffmpegSource as IDisposable)?.Dispose();
        _ffmpegSource = null;
    }
}

/// <summary>视频播放项工厂。</summary>
public interface IVideoPlaybackItemFactory
{
    /// <summary>为指定文件创建播放项。</summary>
    /// <param name="file">媒体文件。</param>
    /// <param name="metadata">已读取的视频元数据，用于按编码选择解码后端；为 null 时按自动模式处理。</param>
    /// <returns>播放项；FFmpeg 不可用时为系统解码器结果。</returns>
    Task<VideoPlaybackItem> CreateAsync(StorageFile file, VideoMetadata? metadata);
}

/// <summary>基于 FFmpegInteropX 的视频播放项工厂。</summary>
public sealed class VideoPlaybackItemFactory : IVideoPlaybackItemFactory
{
    /// <inheritdoc />
    public async Task<VideoPlaybackItem> CreateAsync(StorageFile file, VideoMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(file);

        // 解码相关配置挂在 Video 子配置上：MediaSourceConfig 只是 General/Video/Audio/Subtitles 的聚合容器。
        var config = new MediaSourceConfig();

        // 默认 AutomaticSystemDecoder 会优先用系统解码器；AV1 在本机必须强制 FFmpeg 软解，
        // 其余编码保持自动：系统解码器能硬解时不改变既有行为。
        config.Video.VideoDecoderMode = IsAv1(metadata?.VideoCodec)
            ? VideoDecoderMode.ForceFFmpegSoftwareDecoder
            : VideoDecoderMode.AutomaticSystemDecoder;

        // FFmpeg 的解码线程数默认不是按核心数放开，不显式设置就跑不满多核。
        config.Video.MaxDecoderThreads = (uint)Environment.ProcessorCount;

        try
        {
            var stream = await file.OpenAsync(FileAccessMode.Read);
            var source = await FFmpegMediaSource.CreateFromStreamAsync(stream, config);

            LogDecodeBackend(config.Video.VideoDecoderMode, metadata?.VideoCodec);

            return new VideoPlaybackItem(source.CreateMediaPlaybackItem(), source);
        }
        catch (Exception ex) when (ex is NotSupportedException
                                      or InvalidOperationException
                                      or IOException
                                      or ArgumentException
                                      or UnauthorizedAccessException)
        {
            // 回退路径保证异常文件仍能经系统解码器播放，行为与引入 FFmpeg 前一致。
            Diagnostics.Log(
                $"VIDEODEC|fallback=system|codec={metadata?.VideoCodec}|reason={ex.GetType().Name}");

            return new VideoPlaybackItem(
                new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file)),
                null);
        }
    }

    /// <summary>记录本次实际采用的解码后端，供排查「CPU 高」时确认是否真的走了 FFmpeg。</summary>
    /// <param name="mode">实际生效的解码模式。</param>
    /// <param name="codec">视频编码名称。</param>
    private static void LogDecodeBackend(VideoDecoderMode mode, string? codec) =>
        Diagnostics.Log($"VIDEODEC|codec={codec}|mode={mode}|threads={Environment.ProcessorCount}");

    /// <summary>判断是否为 AV1：MediaClip 返回的 subtype 通常是 av01，少数环境为 av1。</summary>
    private static bool IsAv1(string? codec) =>
        codec is not null
        && (codec.Contains("av01", StringComparison.Ordinal) || codec.Contains("av1", StringComparison.Ordinal));
}
