/**
 * 缩略图服务（M2）。
 * 职责：按请求的尺寸产出图片或视频的缩略图，探测媒体的显示尺寸，
 *      并提供内存与磁盘两级缓存降低重复解码开销。
 * 复用约定：图片经 BitmapDecoder + BitmapTransform 重采样（缩小用 Fant、放大用 Cubic），
 *          绕过 Windows 系统缩略图缓存的低质量 JPEG，
 *          也不使用 BitmapImage.DecodePixelWidth（其插值模式不可控，画质偏软）；
 *          视频使用系统缩略图 API（IThumbnailProvider），因自行解码视频帧成本高且依赖更多编解码器。
 *          尺寸探测只读文件头不解码像素，用于在缩略图到位之前确定宽高比，避免布局从方图跳变，
 *          该读取经 MediaDimensionReader 与后台元数据回填共用同一份实现。
 * 关键约束：解码与重采样是 CPU 密集操作，一律经信号量限流后放到线程池执行；
 *          除「创建位图」这一跳外，所有 await 都用 ConfigureAwait(false) 留在线程池，
 *          回 UI 线程一律经注入的 DispatcherQueue 显式切换（BitmapImage 是 DependencyObject，
 *          必须在 UI 线程创建）——故调用方可在任意线程发起调用，不必从 UI 线程进入。
 *          注意不要改回 ConfigureAwait(true)：中途任一 await 脱离同步上下文后，
 *          后续 true 已无法切回 UI 线程（实测在线程池创建 BitmapImage 抛 0x8001010E）。
 *          SoftwareBitmapSource 直通实验（两轮）均触发 XAML 0xc000027b fail-fast——
 *          该类型在本运行时（XAML 3.2.3.0）不可用，勿再尝试，详见 CreateBitmapOnUiAsync 注释。
 *          不限流会让上百个续体同时排队回 UI 线程，表现为缩略图迟迟不出现。
 *          size 表示显示区的逻辑像素最长边，须先按 RasterizationScale 换算为物理像素再量化到档位：
 *          高 DPI 屏若按逻辑尺寸解码，位图会被放大到 1.5 / 2 倍物理尺寸而发虚；
 *          先量化后换算则会让档位误差被 DPI 成倍放大。物理档位已隐含缩放比，
 *          缩放比变化自然落到不同的缓存键，旧档位条目随滑动过期淘汰。
 *          磁盘缓存（ThumbnailDiskCache）：编码字节在内存缓存之外再持久化一份（LRU 2GB），
 *          命中时跳过「读原图 → 解码 → 重采样 → 编码」全链路，二次浏览与重启后
 *          首屏直接从磁盘读成品字节。磁盘层是纯加速层：任何故障静默退化为重新编码；
 *          清理语义分两种——Invalidate 清内存 + 磁盘（内容真失效），Release 只释放内存位图。
 */

using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
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

    /// <summary>获取指定文件的缩略图；失败时返回 null，取消时抛出 <see cref="OperationCanceledException"/>。</summary>
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

    /// <summary>
    /// 仅释放内存位图，保留磁盘条目。用于切换视图 / 列表瘦身等「回收内存」场景：
    /// 源内容并未失效，磁盘成品仍可随时重建位图；若此处连磁盘一并清除，
    /// 每次切换都会把上一个目录的缓存删光，磁盘层形同虚设（实测 5 轮切换即清空全部条目）。
    /// </summary>
    /// <param name="path">媒体文件完整路径。</param>
    void Release(string path);
}

/// <summary>基于系统缩略图 API 与内存缓存的缩略图服务。</summary>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    /// <summary>放大倍率上限：原图小于显示区时最多放大到该倍数，超过则保留原图。</summary>
    private const double MaxUpscaleFactor = 2.0;

    /// <summary>
    /// 统一解码档位：小于该值的请求（128 / 256 等视图档位）也按该档位解码、缓存与落盘，
    /// 显示端由 Image 控件缩小呈现（缩小无画质损失）。这样磁盘与内存只维护一档主流尺寸：
    /// 条目数从「档位数 × 文件数」降为「文件数」，切换视图档位时全量命中，不再重复解码。
    /// 代价是低档视图的位图内存与重采样成本升高（512² vs 256² 的 4 倍），
    /// 由内存缓存的字节限额与列表头部瘦身兜底；超过该档位的请求（未来大图预览）仍按各自档位缓存。
    /// </summary>
    private const int UnifiedBucket = 512;

    /// <summary>缩放比容差：目标与源的最长边比值在此范围内视为相等，跳过无意义的重采样。</summary>
    private const double SizeTolerance = 0.01;

    /// <summary>
    /// 并发解码上限。重采样与编码是 CPU 密集操作，并发过高既会与磁盘 IO 争抢，
    /// 也会让上百个续体排队回 UI 线程，表现为缩略图迟迟不出现。
    /// </summary>
    private static readonly int DecodeConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// 单条编码的超时上限。编码内部的文件 IO（打开/读取）无内建取消，文件被杀软锁定、
    /// 机械盘坏道重试等会让单条任务永久挂起并占死解码信号量槽位——并发数条挂起即令
    /// 整条缩略图管线静默死亡（实测：提交 200 条后零产出、加载状态机永不收口）。
    /// 超时后放弃该条并释放槽位，管线自愈；界面显示占位图，滚回时重新请求。
    /// </summary>
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;

    /// <summary>磁盘缓存层；命中时跳过全量解码，编码成功后顺手持久化。可为 null（单测 / 显式关闭）。</summary>
    private readonly IThumbnailDiskCache? _diskCache;

    /// <summary>解码节流阀：限制同时进行的重采样数量。</summary>
    private readonly SemaphoreSlim _decodeGate = new(DecodeConcurrency);

    /// <summary>尺寸探测结果；键为文件路径，值随缓存常驻（每条仅两个 int，十万条也才数 MB）。</summary>
    private readonly ConcurrentDictionary<string, (int Width, int Height)> _dimensions =
        new(StringComparer.OrdinalIgnoreCase);

    private double _rasterizationScale = 1.0;

    /// <summary>UI 线程调度队列：BitmapImage 是 XAML DependencyObject，必须在 UI 线程创建，
    /// 非 UI 线程创建后挂到视图树会原生崩溃（crash.log 无托管记录的闪退）。</summary>
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>初始化缩略图服务。</summary>
    /// <param name="cache">内存缓存。</param>
    /// <param name="dispatcherQueue">UI 线程调度队列；为空时取当前线程的队列（单例在 UI 线程构造）。</param>
    /// <param name="diskCache">磁盘缓存；为空时退化为纯内存管线。</param>
    public ThumbnailService(IMemoryCache cache, DispatcherQueue? dispatcherQueue = null, IThumbnailDiskCache? diskCache = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _diskCache = diskCache;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
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
        // 量化结果再向上归一到统一档位：低档请求复用同一套 512 成品（向下兼容）。
        var requestedBucket = ThumbnailSizes.SnapToBucket(Math.Ceiling(size * _rasterizationScale));
        var bucket = requestedBucket <= UnifiedBucket ? UnifiedBucket : requestedBucket;
        var cacheKey = $"{path}|{bucket}";

        if (_cache.TryGetValue(cacheKey, out BitmapImage? cached) && cached is not null)
        {
            LogRatio(size, bucket, 0, cacheHit: true);
            return cached;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();

            // 信号量等待与重采样编码都不依赖 UI 亲和性，续体一律留在线程池：
            // 整页提交时每条会产生多次续体，若全部 ConfigureAwait(true) 回到 UI 线程，
            // 数百次排队会把 UI 线程占满数十秒，表现为加载完成后界面长时间无响应。
            // 仅最后的位图创建需要回到 UI 线程（BitmapImage 是 DependencyObject）。
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            byte[]? encodedBytes;
            var diskHit = false;

            try
            {
                // 磁盘命中（含源文件指纹校验）即可跳过全量解码，磁盘读也在闸门内：
                // 它同样是一次文件 IO，且被 EncodeTimeout 的超时保护覆盖。
                var diskStopwatch = Stopwatch.StartNew();
                encodedBytes = _diskCache is null
                    ? null
                    : await _diskCache.TryGetAsync(path, bucket, cancellationToken).ConfigureAwait(false);
                diskStopwatch.Stop();
                diskHit = encodedBytes is not null;

                // 【临时诊断】区分「磁盘命中但整体仍慢」与「磁盘未命中走全量解码」。
                Diagnostics.Log($"DISK|{bucket}|{(diskHit ? 1 : 0)}|{diskStopwatch.ElapsedMilliseconds}|{path}");

                if (encodedBytes is null)
                {
                    // 超时只在等待侧收口（WaitAsync），不取消内部任务：挂死的 IO 由其自然终局，
                    // 关键是及时释放信号量槽位让管线自愈。内部任务无人等待，不会抛未观察异常。
                    encodedBytes = await Task.Run(() => EncodeThumbnailAsync(path, bucket), cancellationToken)
                        .WaitAsync(EncodeTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    if (encodedBytes is not null && _diskCache is not null)
                    {
                        // 落盘是顺手行为：失败静默、不等待、不占用主路径，下次命中即可回本。
                        _ = _diskCache.StoreAsync(path, bucket, encodedBytes, CancellationToken.None);
                    }
                }
            }
            finally
            {
                _decodeGate.Release();
            }

            if (encodedBytes is null)
            {
                return null;
            }

            // 解码成功但请求已被取消（快速滚动/切换文件夹）时立即按取消收口：
            // 否则仍会带着过期结果继续推进，多次切换后回调洪峰令 UI 线程假死。
            cancellationToken.ThrowIfCancellationRequested();

            // 解码成功但请求已被取消（快速滚动/切换文件夹）时立即按取消收口：
            // 否则仍会带着过期结果回到 UI 线程创建位图，多次切换后回调洪峰令 UI 线程假死。
            cancellationToken.ThrowIfCancellationRequested();

            // BitmapImage 是 DependencyObject，只能在 UI 线程创建。中途的 ConfigureAwait(false)
            // 已令 SynchronizationContext.Current 变为 null，ConfigureAwait(true) 无法切回——
            // 实测在线程池上创建位图抛 0x8001010E（RPC_E_WRONG_THREAD），整页缩略图静默全灭。
            // 故经调度队列显式切回：回调内 SynchronizationContext 为 UI 上下文，
            // CreateBitmapAsync 内部的 await 会稳定停留在 UI 线程。
            // 【回退记录】SoftwareBitmapSource 直通实验两轮均在 XAML 3.2.3.0 上触发
            // 0xc000027b fail-fast（即便 SetBitmapAsync 已回 UI 线程），该类型在本运行时不可用，
            // 二次解码成本改由「分帧提交 + 覆盖层等待」消化（见 GalleryViewModel）。
            var bitmapTask = new TaskCompletionSource<BitmapImage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var bitmapStopwatch = Stopwatch.StartNew();

            if (!_dispatcherQueue.TryEnqueue(() => CreateBitmapOnUiAsync(encodedBytes, bitmapTask)))
            {
                bitmapTask.SetException(new InvalidOperationException("UI 调度队列不可用，无法创建缩略图位图。"));
            }

            var bitmap = await bitmapTask.Task.ConfigureAwait(false);
            bitmapStopwatch.Stop();

            // 【临时诊断】位图创建含 UI 队列排队与解码。
            Diagnostics.Log($"BITMAP|{bucket}|{bitmapStopwatch.ElapsedMilliseconds}|{(diskHit ? 1 : 0)}");

            stopwatch.Stop();

            // Size 为位图字节估算（BGRA4 通道），与缓存 SizeLimit 的字节语义配套：
            // 超限时 MemoryCache 按 LRU 淘汰，防止位图无限累积推高内存与 GC 压力。
            _cache.Set(cacheKey, bitmap, new MemoryCacheEntryOptions
            {
                SlidingExpiration = SlidingExpiration,
                Size = (long)bucket * bucket * 4
            });

            LogRatio(size, bucket, stopwatch.ElapsedMilliseconds, cacheHit: false);
            return bitmap;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException
                                      or TimeoutException
                                      or System.Runtime.InteropServices.COMException)
        {
            // 文件被移动、占用、格式不受支持或编码超时时返回空，由界面显示占位图；
            // COMException 覆盖 WinRT 层的线程亲和与 RPC 类失败，必须收口否则会炸断
            // 调用方 WhenAll 的整页提交链。
            // 【临时诊断】全量记录失败类型与消息摘要：ProBE 可读同一文件而编码失败，
            // 失败环节此前完全黑盒，此处取证后收敛。
            Diagnostics.Log(
                $"THUMBFAIL|{ex.GetType().Name}|hr=0x{ex.HResult:X8}|{ex.Message.Substring(0, Math.Min(96, ex.Message.Length))}|{bucket}|{path}");

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
            // 读取逻辑与后台元数据回填共用 MediaDimensionReader，两处不会各写一遍而漂移。
            var file = await StorageFile.GetFileFromPathAsync(path);
            var size = await MediaDimensionReader.ReadAsync(file, IsVideoFile(path));

            if (size is not { Width: > 0, Height: > 0 })
            {
                return null;
            }

            _dimensions[path] = (size.Value.Width, size.Value.Height);
            return (size.Value.Width, size.Value.Height);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            // 文件被移动、占用或格式不受支持时返回空，宽高比回落到后续位图的实际尺寸。
            // 【临时诊断】记录失败类型：与缩略图编码失败互相印证。
            Diagnostics.Log($"DIMFAIL|{ex.GetType().Name}|{ex.Message.Substring(0, Math.Min(96, ex.Message.Length))}|{path}");

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

        // 磁盘条目同步删除：Invalidate 语义是「源内容已失效」，两层数据必须同时清。
        // 磁盘侧失败静默，残留条目由指纹校验或 LRU 驱逐兜底。
        _diskCache?.Invalidate(path);
    }

    /// <inheritdoc />
    public void Release(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 只清内存位图：条目模板对位图的引用随 ViewModel.Thumbnail 置空断开，
        // 内存缓存条目移除后由字节限额 LRU 接管；磁盘成品保留供滚回 / 切回时秒级重建。
        foreach (var bucket in ThumbnailSizes.DecodeBuckets)
        {
            _cache.Remove($"{path}|{bucket}");
        }
    }

    /// <summary>释放解码节流阀。</summary>
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

    /// <summary>经调度队列在 UI 线程创建位图并经任务源回传；异常一并收口到任务源。</summary>
    /// <param name="bytes">已编码的图像字节。</param>
    /// <param name="completion">位图任务源；以 RunContinuationsAsynchronously 创建，
    /// 防止 UI 回调内同步内联执行下游续体。</param>
    private static async void CreateBitmapOnUiAsync(byte[] bytes, TaskCompletionSource<BitmapImage> completion)
    {
        try
        {
            completion.SetResult(await CreateBitmapAsync(bytes));
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
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
