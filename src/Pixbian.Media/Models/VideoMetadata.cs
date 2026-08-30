/**
 * 视频元数据模型（M4）。
 * 职责：承载由视频文件解析出的容器、视频流与音频流信息，供详情面板与索引库使用。
 * 复用约定：全部为只读记录，由 VideoMetadataReader 统一产出；缺失字段用 null 表示，
 *          界面据此显示占位符，禁止用 0 冒充"无数据"（否则会显示"0 fps"这类误导信息）。
 * 关键约束：Duration、Width、Height 会被回写到索引库的 duration_ms、width、height 列，
 *          供 M5 之后的筛选与排序使用；码率单位统一为 bps，帧率为 fps，采样率为 Hz。
 */

using System.Globalization;

namespace Pixbian.Media.Models;

/// <summary>视频元数据。</summary>
public sealed record VideoMetadata
{
    /// <summary>时长。</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>视频宽度（像素）。</summary>
    public int? Width { get; init; }

    /// <summary>视频高度（像素）。</summary>
    public int? Height { get; init; }

    /// <summary>视频编码名称，如 h264、hevc。</summary>
    public string? VideoCodec { get; init; }

    /// <summary>音频编码名称，如 aac、mp3。</summary>
    public string? AudioCodec { get; init; }

    /// <summary>总码率（bps）。</summary>
    public long? Bitrate { get; init; }

    /// <summary>帧率（fps）。</summary>
    public double? FrameRate { get; init; }

    /// <summary>音频声道数。</summary>
    public int? AudioChannels { get; init; }

    /// <summary>音频采样率（Hz）。</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>容器格式名称，如 mp4、mkv；由文件扩展名推导，非精确容器探测。</summary>
    public string? ContainerFormat { get; init; }

    /// <summary>用于展示的时长文本。</summary>
    public string DurationText => Duration.HasValue
        ? Duration.Value.TotalHours >= 1
            ? Duration.Value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : Duration.Value.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>用于展示的分辨率文本。</summary>
    public string DimensionText => Width.HasValue && Height.HasValue
        ? $"{Width.Value} × {Height.Value}"
        : "—";

    /// <summary>用于展示的码率文本。</summary>
    public string BitrateText => Bitrate.HasValue
        ? (Bitrate.Value / 1_000_000.0).ToString("F1", CultureInfo.CurrentCulture) + " Mbps"
        : "—";

    /// <summary>用于展示的帧率文本。</summary>
    public string FrameRateText => FrameRate.HasValue
        ? $"{FrameRate.Value.ToString("F2", CultureInfo.CurrentCulture)} fps"
        : "—";

    /// <summary>用于展示的音频信息文本。</summary>
    public string AudioText
    {
        get
        {
            if (AudioCodec is null)
            {
                return "—";
            }

            var channels = AudioChannels.HasValue
                ? $" · {AudioChannels.Value.ToString(CultureInfo.CurrentCulture)} 声道"
                : string.Empty;

            var sampleRate = AudioSampleRate.HasValue
                ? $" · {(AudioSampleRate.Value / 1000.0).ToString("F1", CultureInfo.CurrentCulture)} kHz"
                : string.Empty;

            return $"{AudioCodec}{channels}{sampleRate}";
        }
    }
}
