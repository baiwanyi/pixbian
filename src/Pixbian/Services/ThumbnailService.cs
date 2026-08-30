/**
 * 缩略图服务（M2）。
 * 职责：按请求的尺寸产出图片或视频的缩略图，并提供内存缓存以降低滚动时的重复解码开销。
 * 复用约定：图片经 BitmapDecoder + Fant 重采样高质量降采样，绕过 Windows 系统缩略图缓存的低质量 JPEG，
 *          也不使用 BitmapImage.DecodePixelWidth（其插值模式不可控，画质偏软）；
 *          视频使用系统缩略图 API（IThumbnailProvider），因自行解码视频帧成本高且依赖更多编解码器。
 *          请求尺寸一律经 ThumbnailSizes.SnapToBucket 量化到固定档位，
 *          以控制内存缓存条目规模；档位过疏会让位图被拉伸，过密会浪费内存。
 * 关键约束：返回的 BitmapImage 必须在 UI 线程创建，故本服务的异步方法一律不使用 ConfigureAwait(false)，
 *          以保证 await 之后回到调用方的 UI 同步上下文；调用方必须从 UI 线程发起调用。
 *          size 表示显示区的逻辑像素最长边，解码时按 RasterizationScale 换算为物理像素，
 *          否则高 DPI 屏会把位图拉伸到 1.5 / 2 倍物理尺寸而发虚；缩放比变化经缓存代次整体失效，
 *          旧代次条目无需枚举，随滑动过期自然淘汰。
 *          磁盘缓存与后台预取将在 M3 随缩略图管线统一引入。
 */

using System.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Pixbian.Services;

/// <summary>缩略图服务。</summary>
public interface IThumbnailService
{
    /// <summary>
    /// 当前显示缩放比（1.0 表示 96 DPI）。
    /// 由主窗口按 XamlRoot.RasterizationScale 同步，用于把请求尺寸换算为物理像素。
    /// </summary>
    double RasterizationScale { get; set; }

    /// <summary>获取指定文件的缩略图；失败时返回 null。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="size">显示区最长边（逻辑像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BitmapImage?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken cancellationToken = default);

    /// <summary>供绑定与委托使用的缩略图加载入口，等价于 <see cref="GetThumbnailAsync"/>。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="size">显示区最长边（逻辑像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BitmapImage?> LoadThumbnailAsyncCore(
        string path,
        int size,
        CancellationToken cancellationToken) =>
        GetThumbnailAsync(path, size, cancellationToken);

    /// <summary>移除指定文件的缓存条目。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    void Invalidate(string path);
}

/// <summary>基于系统缩略图 API 与内存缓存的缩略图服务。</summary>
public sealed class ThumbnailService : IThumbnailService
{
    private const int MaxCachedEntries = 2000;

    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;
    private double _rasterizationScale = 1.0;
    private int _generation;

    /// <summary>初始化缩略图服务。</summary>
    /// <param name="cache">内存缓存。</param>
    public ThumbnailService(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <inheritdoc />
    public double RasterizationScale
    {
        get => _rasterizationScale;
        set
        {
            var scale = Math.Clamp(value, 1.0, 4.0);

            if (Math.Abs(scale - _rasterizationScale) < 0.01)
            {
                return;
            }

            _rasterizationScale = scale;

            // 缩放比变化后已缓存位图的物理分辨率不再匹配，提升代次让缓存键整体失效。
            _generation++;
        }
    }

    /// <inheritdoc />
    public async Task<BitmapImage?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bucket = ThumbnailSizes.SnapToBucket(size);
        var cacheKey = $"{path}|{bucket}|{_generation}";

        if (_cache.TryGetValue(cacheKey, out BitmapImage? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var bitmap = await DecodeThumbnailAsync(path, bucket).ConfigureAwait(true);

            if (bitmap is null)
            {
                return null;
            }

            _cache.Set(cacheKey, bitmap, new MemoryCacheEntryOptions
            {
                SlidingExpiration = SlidingExpiration,
                Size = 1
            });

            return bitmap;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            // 文件被移动、占用或格式不受支持时返回空，由界面显示占位图。
            return null;
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <inheritdoc />
    public void Invalidate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        foreach (var bucket in ThumbnailSizes.DecodeBuckets)
        {
            _cache.Remove($"{path}|{bucket}|{_generation}");
        }
    }

    /// <summary>解码缩略图：图片直接解码原图降采样，视频使用系统缩略图 API。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="logicalSize">显示区最长边（逻辑像素）。</param>
    private async Task<BitmapImage?> DecodeThumbnailAsync(string path, int logicalSize)
    {
        // 按物理像素请求：位图若只有逻辑尺寸，高 DPI 屏放大到物理像素时必然发虚。
        var physicalSize = (int)Math.Ceiling(logicalSize * _rasterizationScale);
        var file = await StorageFile.GetFileFromPathAsync(path);

        return IsVideoFile(path)
            ? await DecodeVideoThumbnailAsync(file, physicalSize)
            : await DecodeImageThumbnailAsync(file, physicalSize);
    }

    /// <summary>图片缩略图：用 WIC 解码器按高质量重采样降采样，再无损中转给 BitmapImage。</summary>
    /// <param name="file">图片文件。</param>
    /// <param name="physicalSize">显示区最长边（物理像素）。</param>
    private static async Task<BitmapImage?> DecodeImageThumbnailAsync(StorageFile file, int physicalSize)
    {
        using var stream = await file.OpenReadAsync();

        if (stream is null || stream.Size == 0)
        {
            return null;
        }

        var decoder = await BitmapDecoder.CreateAsync(stream);

        // OrientedPixel* 已计入 EXIF 方向，用它算目标尺寸，竖拍的横图才不会被压反。
        var target = ComputeTargetSize(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, physicalSize);

        // 原图本身不超过目标时按原图尺寸输出，放大只会更虚，且省去中转开销。
        if (target is null)
        {
            stream.Seek(0);

            var original = new BitmapImage();
            await original.SetSourceAsync(stream);
            return original;
        }

        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = (uint)target.Value.Width,
                ScaledHeight = (uint)target.Value.Height,

                // Fant 重采样：大幅缩小时画质显著优于默认的双线性（Linear），
                // 后者在 4000px → 384px 这类比例下会丢细节并产生锯齿。
                // BitmapImage.DecodePixelWidth 无法指定插值模式，这正是它画质偏软的主因。
                InterpolationMode = BitmapInterpolationMode.Fant
            },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        // BitmapImage 不能直接承载 SoftwareBitmap，故把降采样结果编码到内存流再交给它解码。
        // JPEG 无 alpha，用 BMP 编码（纯内存拷贝，开销可忽略）；其余格式可能有 alpha，
        // 用 PNG 保留，否则透明区会被预乘成黑色。
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            decoder.DecoderInformation.CodecId == BitmapDecoder.JpegDecoderId
                ? BitmapEncoder.BmpEncoderId
                : BitmapEncoder.PngEncoderId,
            encoded);

        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();

        encoded.Seek(0);

        // 不设 DecodePixel*：流已是最终尺寸，再降采样等于二次缩放。
        var bitmapImage = new BitmapImage();
        await bitmapImage.SetSourceAsync(encoded);
        return bitmapImage;
    }

    /// <summary>按最长边约束等比计算目标尺寸；原图不大于目标时返回 null，表示无需降采样。</summary>
    /// <param name="sourceWidth">定向后原图宽度。</param>
    /// <param name="sourceHeight">定向后原图高度。</param>
    /// <param name="longestSide">目标最长边（物理像素）。</param>
    private static (int Width, int Height)? ComputeTargetSize(uint sourceWidth, uint sourceHeight, int longestSide)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            return null;
        }

        if (sourceWidth <= longestSide && sourceHeight <= longestSide)
        {
            return null;
        }

        var scale = (double)longestSide / Math.Max(sourceWidth, sourceHeight);

        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    /// <summary>视频缩略图：使用系统 IThumbnailProvider，自行解码视频帧成本高且依赖更多编解码器。</summary>
    /// <param name="file">视频文件。</param>
    /// <param name="physicalSize">显示区最长边（物理像素）。</param>
    private static async Task<BitmapImage?> DecodeVideoThumbnailAsync(StorageFile file, int physicalSize)
    {
        // 必须用 SingleItem：PicturesView / VideosView 会返回系统居中裁剪过的方形缩略图，
        // 位图宽高比恒为 1，等高布局将退化成等宽格子；SingleItem 保持原图纵横比。
        // ResizeThumbnail 让系统精确缩放到请求尺寸，否则系统返回缓存中最接近的档位，
        // 该档位可能小于请求值，位图被拉伸后就会发虚。
        using var systemThumbnail = await file.GetThumbnailAsync(
            ThumbnailMode.SingleItem,
            (uint)physicalSize,
            ThumbnailOptions.ResizeThumbnail);

        if (systemThumbnail is null || systemThumbnail.Size == 0)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(systemThumbnail);
        return bitmap;
    }

    private static bool IsVideoFile(string path) =>
        Core.Services.MediaFileClassifier.Classify(path) == Core.Models.MediaKind.Video;
}
