/**
 * 缩略图服务（M2）。
 * 职责：按请求的尺寸产出图片或视频的缩略图，探测媒体的显示尺寸，并提供内存缓存降低重复解码开销。
 * 复用约定：图片经 BitmapDecoder + BitmapTransform 重采样（缩小用 Fant、放大用 Cubic），
 *          绕过 Windows 系统缩略图缓存的低质量 JPEG，
 *          也不使用 BitmapImage.DecodePixelWidth（其插值模式不可控，画质偏软）；
 *          视频使用系统缩略图 API（IThumbnailProvider），因自行解码视频帧成本高且依赖更多编解码器。
 *          尺寸探测只读文件头不解码像素，用于在缩略图到位之前确定宽高比，避免布局从方图跳变。
 * 关键约束：解码与重采样是 CPU 密集操作，一律经信号量限流后放到线程池执行，只把编码字节交回 UI 线程；
 *          BitmapImage 是 DependencyObject，必须在 UI 线程创建，故本服务的 await 一律保留同步上下文
 *          （ConfigureAwait(true)），调用方必须从 UI 线程发起调用。
 *          不限流会让上百个续体同时排队回 UI 线程，表现为缩略图迟迟不出现。
 *          size 表示显示区的逻辑像素最长边，须先按 RasterizationScale 换算为物理像素再量化到档位：
 *          高 DPI 屏若按逻辑尺寸解码，位图会被放大到 1.5 / 2 倍物理尺寸而发虚；
 *          先量化后换算则会让档位误差被 DPI 成倍放大。物理档位已隐含缩放比，
 *          缩放比变化自然落到不同的缓存键，旧档位条目随滑动过期淘汰。
 *          磁盘缓存与后台预取将在 M3 随缩略图管线统一引入。
 */

using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Stopwatch = System.Diagnostics.Stopwatch;

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

    /// <summary>读取媒体的显示尺寸（像素）；失败或不支持时返回 null。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<(int Width, int Height)?> GetDimensionsAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>移除指定文件的缓存条目。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    void Invalidate(string path);
}

/// <summary>基于系统缩略图 API 与内存缓存的缩略图服务。</summary>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    private const int MaxCachedEntries = 2000;

    /// <summary>放大倍率上限：原图小于显示区时最多放大到该倍数，超过则保留原图。</summary>
    private const double MaxUpscaleFactor = 2.0;

    /// <summary>缩放比容差：目标与源的最长边比值在此范围内视为相等，跳过无意义的重采样。</summary>
    private const double SizeTolerance = 0.01;

    /// <summary>
    /// 并发解码上限。重采样与编码是 CPU 密集操作，并发过高既会与磁盘 IO 争抢，
    /// 也会让上百个续体排队回 UI 线程，表现为缩略图迟迟不出现。
    /// </summary>
    private static readonly int DecodeConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;

    /// <summary>解码节流阀：限制同时进行的重采样数量。</summary>
    private readonly SemaphoreSlim _decodeGate = new(DecodeConcurrency);

    /// <summary>尺寸探测结果；键为文件路径，值随缓存常驻（每条仅两个 int，十万条也才数 MB）。</summary>
    private readonly ConcurrentDictionary<string, (int Width, int Height)> _dimensions =
        new(StringComparer.OrdinalIgnoreCase);

    private double _rasterizationScale = 1.0;

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
        set => _rasterizationScale = Math.Clamp(value, 1.0, 4.0);
    }

    /// <inheritdoc />
    public async Task<BitmapImage?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 先换算物理像素再量化：顺序颠倒会让档位误差被 DPI 成倍放大。
        var bucket = ThumbnailSizes.SnapToBucket(Math.Ceiling(size * _rasterizationScale));
        var cacheKey = $"{path}|{bucket}";

        if (_cache.TryGetValue(cacheKey, out BitmapImage? cached) && cached is not null)
        {
            LogRatio(size, bucket, 0, cacheHit: true);
            return cached;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();

            // 重采样与编码是 CPU 密集操作，一律放到线程池并限流；留在调用线程会让 UI 线程
            // 被上百个解码任务轮流阻塞，这正是缩略图端到端延迟高达数秒的根因。
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(true);

            byte[]? encodedBytes;

            try
            {
                encodedBytes = await Task.Run(() => EncodeThumbnailAsync(path, bucket), cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                _decodeGate.Release();
            }

            if (encodedBytes is null)
            {
                return null;
            }

            // BitmapImage 是 DependencyObject，只能在 UI 线程创建；此处已回到调用方的 UI 上下文。
            var bitmap = await CreateBitmapAsync(encodedBytes).ConfigureAwait(true);
            stopwatch.Stop();

            _cache.Set(cacheKey, bitmap, new MemoryCacheEntryOptions
            {
                SlidingExpiration = SlidingExpiration,
                Size = 1
            });

            LogRatio(size, bucket, stopwatch.ElapsedMilliseconds, cacheHit: false);
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
    public async Task<(int Width, int Height)?> GetDimensionsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_dimensions.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var size = IsVideoFile(path)
                ? await ReadVideoDimensionsAsync(file)
                : await ReadImageDimensionsAsync(file);

            if (size is not { Width: > 0, Height: > 0 })
            {
                return null;
            }

            _dimensions[path] = size.Value;
            return size;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            // 文件被移动、占用或格式不受支持时返回空，宽高比回落到后续位图的实际尺寸。
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
            _cache.Remove($"{path}|{bucket}");
        }
    }

    /// <summary>释放解码节流阀。本服务注册为单例，由容器在应用关闭时释放。</summary>
    public void Dispose() => _decodeGate.Dispose();

    /// <summary>记录位图物理像素相对显示区物理像素的比值，用于评估显示端缩放带来的画质损失。</summary>
    /// <param name="logicalSize">请求的最长边（逻辑像素）。</param>
    /// <param name="bucket">实际解码的最长边（物理像素）。</param>
    /// <param name="elapsedMs">解码耗时（毫秒）；缓存命中时为 0。</param>
    /// <param name="cacheHit">是否命中内存缓存。</param>
    private void LogRatio(int logicalSize, int bucket, long elapsedMs, bool cacheHit)
    {
        var physicalTarget = Math.Ceiling(logicalSize * _rasterizationScale);
        var ratio = physicalTarget > 0 ? bucket / physicalTarget : 0d;

        Diagnostics.Log(
            $"THUMB|{logicalSize}|{_rasterizationScale:F3}|{bucket}|{ratio:F3}|{elapsedMs}|{(cacheHit ? 1 : 0)}");
    }

    /// <summary>图片尺寸：取 OrientedPixel*（已计入 EXIF 方向），与缩略图解码结果完全一致。</summary>
    /// <param name="file">图片文件。</param>
    private static async Task<(int Width, int Height)?> ReadImageDimensionsAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();

        if (stream is null || stream.Size == 0)
        {
            return null;
        }

        // BitmapDecoder 只读文件头即可给出尺寸，不解码像素，故远快于解码缩略图。
        var decoder = await BitmapDecoder.CreateAsync(stream);

        return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    /// <summary>视频尺寸：WIC 不支持视频容器，改读系统视频属性并按旋转标记还原宽高。</summary>
    /// <param name="file">视频文件。</param>
    private static async Task<(int Width, int Height)?> ReadVideoDimensionsAsync(StorageFile file)
    {
        var properties = await file.Properties.GetVideoPropertiesAsync();

        if (properties.Width is not > 0 || properties.Height is not > 0)
        {
            return null;
        }

        var width = (int)properties.Width;
        var height = (int)properties.Height;

        // 手机竖拍视频的帧数据横向存储，靠旋转标记还原为竖屏；不处理旋转会让宽高比反过来，
        // 比不预取更糟。180 度不改变宽高比，无需交换。
        return properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270
            ? (height, width)
            : (width, height);
    }

    /// <summary>在线程池解码并重采样，返回编码后的字节数组；全部失败时返回 null。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="physicalSize">显示区最长边（物理像素，已量化到档位）。</param>
    /// <remarks>
    /// 本方法只做 CPU 密集的解码/重采样/编码，不触碰任何 UI 类型（BitmapImage 是
    /// DependencyObject）；编码字节交给调用方在 UI 线程转成 BitmapImage。
    /// </remarks>
    private static async Task<byte[]?> EncodeThumbnailAsync(string path, int physicalSize)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);

        return IsVideoFile(path)
            ? await EncodeVideoThumbnailAsync(file, physicalSize)
            : await EncodeImageThumbnailAsync(file, physicalSize);
    }

    /// <summary>在 UI 线程把编码字节转成 BitmapImage（DependencyObject 须由 UI 线程创建）。</summary>
    /// <param name="bytes">已编码的图像字节。</param>
    private static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();

            // 必须先脱离流，否则 writer 释放时会连带关闭尚未使用的 stream。
            writer.DetachStream();
        }

        stream.Seek(0);

        // 不设 DecodePixel*：流已是最终尺寸，再降采样等于二次缩放。
        var bitmapImage = new BitmapImage();
        await bitmapImage.SetSourceAsync(stream);
        return bitmapImage;
    }

    /// <summary>图片缩略图：在线程池用 WIC 解码器高质量重采样，再编码为字节数组返回。</summary>
    /// <param name="file">图片文件。</param>
    /// <param name="physicalSize">显示区最长边（物理像素）。</param>
    private static async Task<byte[]?> EncodeImageThumbnailAsync(StorageFile file, int physicalSize)
    {
        using var stream = await file.OpenReadAsync();

        if (stream is null || stream.Size == 0)
        {
            return null;
        }

        var decoder = await BitmapDecoder.CreateAsync(stream);

        // OrientedPixel* 已计入 EXIF 方向，用它算目标尺寸，竖拍的横图才不会被压反。
        var target = ComputeTargetSize(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, physicalSize);

        // 无需重采样时直接按原图编码，省去重采样与一遍解码开销；保持原图像素即最高质量。
        if (target is null)
        {
            stream.Seek(0);
            return await ReadAllBytesAsync(stream);
        }

        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = (uint)target.Value.Width,
                ScaledHeight = (uint)target.Value.Height,

                // 插值模式由缩放方向决定：缩小用 Fant、放大用 Cubic，优于默认的双线性。
                InterpolationMode = target.Value.Mode
            },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        // BitmapImage 不能直接承载 SoftwareBitmap，故把降采样结果编码到内存再返回字节数组，
        // 由调用方在 UI 线程转成 BitmapImage。JPEG 无 alpha 用 BMP（纯拷贝），其余用 PNG 保 alpha。
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            decoder.DecoderInformation.CodecId == BitmapDecoder.JpegDecoderId
                ? BitmapEncoder.BmpEncoderId
                : BitmapEncoder.PngEncoderId,
            encoded);

        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();

        encoded.Seek(0);
        return await ReadAllBytesAsync(encoded);
    }

    /// <summary>把随机访问流的全部内容读取为字节数组。</summary>
    /// <param name="stream">可读流。</param>
    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var length = (uint)stream.Size;
        using var reader = new DataReader(stream.GetInputStreamAt(0));

        await reader.LoadAsync(length);

        // ReadBytes 填充调用方提供的缓冲区，不返回数组。
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>按最长边约束等比计算目标尺寸与插值模式；无需重采样时返回 null。</summary>
    /// <param name="sourceWidth">定向后原图宽度。</param>
    /// <param name="sourceHeight">定向后原图高度。</param>
    /// <param name="longestSide">目标最长边（物理像素）。</param>
    private static (int Width, int Height, BitmapInterpolationMode Mode)? ComputeTargetSize(
        uint sourceWidth,
        uint sourceHeight,
        int longestSide)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            return null;
        }

        var scale = (double)longestSide / Math.Max(sourceWidth, sourceHeight);

        if (Math.Abs(scale - 1.0) < SizeTolerance)
        {
            return null;
        }

        // 放大超过上限后插值只能凭空造像素，清晰度不再改善，却让位图内存与解码成本成倍增长，
        // 故保留原图由显示端拉伸。
        if (scale > MaxUpscaleFactor)
        {
            return null;
        }

        // 缩小时 Fant 显著优于默认的双线性（Linear）：后者在 4000px → 384px 这类比例下
        // 会丢细节并产生锯齿；放大时 Fant 偏软，Cubic 的边缘更锐利。
        var mode = scale < 1.0 ? BitmapInterpolationMode.Fant : BitmapInterpolationMode.Cubic;

        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)),
            mode);
    }

    /// <summary>视频缩略图：使用系统 IThumbnailProvider，自行解码视频帧成本高且依赖更多编解码器。</summary>
    /// <param name="file">视频文件。</param>
    /// <param name="physicalSize">显示区最长边（物理像素）。</param>
    private static async Task<byte[]?> EncodeVideoThumbnailAsync(StorageFile file, int physicalSize)
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

        return await ReadAllBytesAsync(systemThumbnail);
    }

    private static bool IsVideoFile(string path) =>
        Core.Services.MediaFileClassifier.Classify(path) == Core.Models.MediaKind.Video;
}
