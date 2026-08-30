/**
 * 媒体文件分类器模块。
 * 职责：依据文件扩展名判定媒体类型，供索引扫描与文件夹监控共用同一份格式白名单。
 * 复用约定：扩展名集合使用大小写不敏感的 FrozenSet，匹配复杂度 O(1) 且无装箱；
 *          HEIC / AVIF 依赖系统解码扩展，能识别格式不代表一定能解码，解码失败由 M3 的 Imaging 层另行处理。
 * 关键约束：新增格式必须同步评估解码器可用性，仅登记扩展名会导致索引中出现无法预览的条目；
 *          分类结果 Other 表示不支持，调用方应据此跳过而非入库。
 */

using System.Collections.Frozen;
using System.IO;
using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>依据扩展名判定媒体类型的分类器。</summary>
public static class MediaFileClassifier
{
    private static readonly FrozenSet<string> ImageExtensions = new[]
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".heics", ".heifs", ".avif", ".ico", ".wdp", ".jxr",
        ".arw", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".dng", ".orf", ".rw2", ".pef",
        ".raf", ".raw", ".srf", ".sr2", ".x3f", ".3fr", ".mef", ".mrw", ".r3d", ".erf",
        ".kdc", ".dcr", ".mos", ".ptx", ".rpe", ".rwl"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> VideoExtensions = new[]
    {
        ".mp4", ".m4v", ".mov", ".qt", ".avi", ".mkv", ".wmv", ".flv", ".f4v", ".webm",
        ".mpg", ".mpeg", ".mpe", ".m2v", ".ts", ".mts", ".m2ts", ".3gp", ".3g2",
        ".rmvb", ".rm", ".asf", ".vob", ".divx", ".ogv", ".ogx"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>判定文件所属媒体类型。</summary>
    /// <param name="fileName">文件名或路径，仅取其中的扩展名部分参与判定。</param>
    /// <returns>命中图片或视频白名单时返回对应类型，否则返回 <see cref="MediaKind.Other"/>。</returns>
    /// <exception cref="ArgumentException">文件名为空白时抛出。</exception>
    public static MediaKind Classify(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extension = Path.GetExtension(fileName);
        if (extension.Length == 0)
        {
            return MediaKind.Other;
        }

        if (ImageExtensions.Contains(extension))
        {
            return MediaKind.Image;
        }

        return VideoExtensions.Contains(extension) ? MediaKind.Video : MediaKind.Other;
    }

    /// <summary>判断文件是否为受支持的媒体文件。</summary>
    /// <param name="fileName">文件名或路径。</param>
    /// <returns>属于图片或视频时返回 true。</returns>
    public static bool IsSupported(string fileName)
    {
        try
        {
            return Classify(fileName) != MediaKind.Other;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
