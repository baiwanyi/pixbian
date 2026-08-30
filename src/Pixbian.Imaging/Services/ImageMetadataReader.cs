/**
 * 图像元数据读取服务（M3）。
 * 职责：用 MetadataExtractor 解析图片文件的 EXIF/IPTC 信息，产出统一的 ImageMetadata。
 * 复用约定：先尝试解析结构化的 ExifSubIfdDirectory，缺失时再回落到 ExifIfd0Directory；
 *          曝光时间与光圈多为有理数（Rational），需单独处理分母为 0 的脏数据。
 * 关键约束：读取失败（文件损坏、格式不支持、文件被占用）一律返回 null，由调用方显示占位，
 *          绝不允许把异常抛到界面层导致查看器崩溃；
 *          GPS 只判定"是否存在"，不读取具体坐标值，避免敏感信息扩散。
 */

using System.Globalization;
using System.IO;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Pixbian.Imaging.Models;

// GetString / GetRational / GetInt32 是 MetadataExtractor 命名空间下的扩展方法，必须引入该命名空间；
// 但同时 MetadataExtractor.Directory 与 System.IO.Directory 同名，故本文件中一律使用别名避免歧义。
using MetadataDirectory = MetadataExtractor.Directory;

namespace Pixbian.Imaging.Services;

/// <summary>图像元数据读取服务。</summary>
public interface IImageMetadataReader
{
    /// <summary>读取图片元数据；失败时返回 null。</summary>
    /// <param name="path">图片文件完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ImageMetadata?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>基于 MetadataExtractor 的元数据读取服务。</summary>
public sealed class ImageMetadataReader : IImageMetadataReader
{
    /// <inheritdoc />
    public async Task<ImageMetadata?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            // 本类与 MetadataExtractor.ImageMetadataReader 同名，故此处必须使用完全限定名，
            // 否则会递归调用自身导致栈溢出。
            var directories = await Task
                .Run(() => MetadataExtractor.ImageMetadataReader.ReadMetadata(path), cancellationToken)
                .ConfigureAwait(false);

            return Build(directories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ImageProcessingException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>由已解析的目录集合构建元数据。</summary>
    private static ImageMetadata Build(IReadOnlyList<MetadataDirectory> directories)
    {
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

        Rational? widthRational = subIfd?.GetRational(ExifSubIfdDirectory.TagExifImageWidth)
            ?? ifd0?.GetRational(ExifIfd0Directory.TagImageWidth);

        Rational? heightRational = subIfd?.GetRational(ExifSubIfdDirectory.TagExifImageHeight)
            ?? ifd0?.GetRational(ExifIfd0Directory.TagImageHeight);

        var takenAt = ReadTakenAt(subIfd, ifd0);

        return new ImageMetadata
        {
            Width = ToInt(widthRational),
            Height = ToInt(heightRational),
            TakenAt = takenAt,
            CameraMake = TrimOrNull(ifd0?.GetString(ExifIfd0Directory.TagMake)),
            CameraModel = TrimOrNull(ifd0?.GetString(ExifIfd0Directory.TagModel)),
            LensModel = TrimOrNull(subIfd?.GetString(ExifSubIfdDirectory.TagLensModel)),
            Aperture = ToDouble(subIfd?.GetRational(ExifSubIfdDirectory.TagFNumber)
                ?? subIfd?.GetRational(ExifSubIfdDirectory.TagAperture)),
            ShutterSpeed = ToDouble(subIfd?.GetRational(ExifSubIfdDirectory.TagExposureTime)),
            Iso = ReadInt32(subIfd, ExifSubIfdDirectory.TagIsoEquivalent)
                ?? ReadInt32(subIfd, ExifSubIfdDirectory.TagIsoSpeed),
            FocalLength = ToDouble(subIfd?.GetRational(ExifSubIfdDirectory.TagFocalLength)),
            Orientation = ReadInt32(ifd0, ExifIfd0Directory.TagOrientation),
            Software = TrimOrNull(ifd0?.GetString(ExifIfd0Directory.TagSoftware)),
            HasGps = gps is not null && gps.TagCount > 0
        };
    }

    /// <summary>解析拍摄时间；EXIF 存储的是本地时间且不含时区，按本地时区解释。</summary>
    private static DateTimeOffset? ReadTakenAt(
        ExifSubIfdDirectory? subIfd,
        ExifIfd0Directory? ifd0)
    {
        // 优先使用 DateTimeOriginal（拍摄时刻），缺失时回落到 DateTime（文件写入时刻）。
        var text = subIfd?.GetString(ExifSubIfdDirectory.TagDateTimeOriginal)
            ?? subIfd?.GetString(ExifSubIfdDirectory.TagDateTimeDigitized)
            ?? ifd0?.GetString(ExifIfd0Directory.TagDateTime);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // EXIF 时间格式为 "yyyy:MM:dd HH:mm:ss"，用 InvariantCulture 指定确切格式解析，
        // 避免把冒号分隔的日期交给通用解析器，也避免受当前区域设置影响。
        return DateTimeOffset.TryParseExact(
            text.Trim(),
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>读取整数标签；标签缺失或类型不符时返回 null。</summary>
    private static int? ReadInt32(MetadataDirectory? directory, int tagType)
    {
        if (directory is null)
        {
            return null;
        }

        return directory.TryGetInt32(tagType, out var value) ? value : null;
    }

    private static int? ToInt(Rational? rational) =>
        rational is { Denominator: not 0 } r ? (int)Math.Round(r.ToDouble()) : null;

    private static double? ToDouble(Rational? rational) =>
        rational is { Denominator: not 0 } r ? r.ToDouble() : null;

    private static string? TrimOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
