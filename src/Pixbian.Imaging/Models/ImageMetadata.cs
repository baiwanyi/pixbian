/**
 * 图像元数据模型（M3）。
 * 职责：承载由图片文件解析出的拍摄参数与设备信息，供详情面板展示。
 * 复用约定：全部为只读记录，由 ImageMetadataReader 统一产出，界面层只做展示不做加工；
 *          缺失字段统一用 null 表示，界面据此显示占位符，禁止用 0 或空串冒充"无数据"。
 * 关键约束：GPS 坐标属于敏感个人信息（PIPL），默认仅用于展示且不做持久化到索引库，
 *          展示时应提供脱敏选项，禁止在日志或局域网共享中输出完整坐标。
 */

using System.Globalization;

namespace Pixbian.Imaging.Models;

/// <summary>图像元数据。</summary>
public sealed record ImageMetadata
{
    /// <summary>宽度（像素）。</summary>
    public int? Width { get; init; }

    /// <summary>高度（像素）。</summary>
    public int? Height { get; init; }

    /// <summary>拍摄时间（由 EXIF 解析，按本地时区解释）。</summary>
    public DateTimeOffset? TakenAt { get; init; }

    /// <summary>相机制造商。</summary>
    public string? CameraMake { get; init; }

    /// <summary>相机型号。</summary>
    public string? CameraModel { get; init; }

    /// <summary>镜头型号。</summary>
    public string? LensModel { get; init; }

    /// <summary>光圈值（F 值）。</summary>
    public double? Aperture { get; init; }

    /// <summary>快门速度（秒）。</summary>
    public double? ShutterSpeed { get; init; }

    /// <summary>ISO 感光度。</summary>
    public int? Iso { get; init; }

    /// <summary>焦距（毫米）。</summary>
    public double? FocalLength { get; init; }

    /// <summary>EXIF 方向标记（1–8）。</summary>
    public int? Orientation { get; init; }

    /// <summary>是否包含 GPS 坐标。</summary>
    public bool HasGps { get; init; }

    /// <summary>相机厂商的软件或固件版本。</summary>
    public string? Software { get; init; }

    /// <summary>用于展示的光圈文本。</summary>
    public string ApertureText => Aperture.HasValue
        ? $"f/{Aperture.Value.ToString("F1", CultureInfo.CurrentCulture)}"
        : "—";

    /// <summary>用于展示的快门文本；小于 1 秒时显示为分数形式。</summary>
    public string ShutterText => ShutterSpeed.HasValue
        ? FormatShutter(ShutterSpeed.Value)
        : "—";

    /// <summary>用于展示的焦距文本。</summary>
    public string FocalLengthText => FocalLength.HasValue
        ? $"{FocalLength.Value.ToString("F0", CultureInfo.CurrentCulture)} mm"
        : "—";

    /// <summary>用于展示的 ISO 文本。</summary>
    public string IsoText => Iso.HasValue
        ? Iso.Value.ToString(CultureInfo.CurrentCulture)
        : "—";

    /// <summary>用于展示的像素尺寸文本。</summary>
    public string DimensionText => Width.HasValue && Height.HasValue
        ? $"{Width.Value} × {Height.Value}"
        : "—";

    /// <summary>把秒数格式化为摄影惯例的快门文本。</summary>
    private static string FormatShutter(double seconds) =>
        seconds >= 1
            ? $"{seconds.ToString("F1", CultureInfo.CurrentCulture)} 秒"
            : $"1/{Math.Round(1 / seconds).ToString("F0", CultureInfo.CurrentCulture)} 秒";
}
