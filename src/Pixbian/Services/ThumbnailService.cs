/**
 * 缩略图服务（M2）。
 * 职责：按请求的尺寸产出图片或视频的缩略图，并提供内存缓存以降低滚动时的重复解码开销。
 * 复用约定：优先使用 Windows 存储 API 的 GetThumbnailAsync，可直接复用系统缩略图缓存，
 *          比自行解码整幅图像快一个数量级；系统缩略图不可用时回退为按目标宽度降采样解码。
 * 关键约束：返回的 BitmapImage 必须在 UI 线程创建，故本服务的异步方法一律不使用 ConfigureAwait(false)，
 *          以保证 await 之后回到调用方的 UI 同步上下文；调用方必须从 UI 线程发起调用。
 *          磁盘缓存与后台预取将在 M3 随缩略图管线统一引入。
 */

using System.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Display;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Pixbian.Services;

/// <summary>缩略图服务。</summary>
public interface IThumbnailService
{
    /// <summary>获取指定文件的缩略图；失败时返回 null。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="size">缩略图边长（像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BitmapImage?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken cancellationToken = default);

    /// <summary>供绑定与委托使用的缩略图加载入口，等价于 <see cref="GetThumbnailAsync"/>。</summary>
    /// <param name="path">媒体文件完整路径。</param>
    /// <param name="size">缩略图边长（像素）。</param>
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

    /// <summary>初始化缩略图服务。</summary>
    /// <param name="cache">内存缓存。</param>
    public ThumbnailService(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<BitmapImage?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var cacheKey = $"{path}|{size}";

        if (_cache.TryGetValue(cacheKey, out BitmapImage? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var bitmap = await DecodeThumbnailAsync(path, size).ConfigureAwait(true);

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

        foreach (var size in new[] { 96, 144, 192, 256, 320 })
        {
            _cache.Remove($"{path}|{size}");
        }
    }

    /// <summary>解码缩略图：先取系统缩略图，失败则降采样解码原图。</summary>
    private static async Task<BitmapImage?> DecodeThumbnailAsync(string path, int size)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);

        // 必须用 SingleItem：PicturesView / VideosView 会返回系统居中裁剪过的方形缩略图，
        // 位图宽高比恒为 1，等高布局将退化成等宽格子；SingleItem 保持原图纵横比。
        var mode = ThumbnailMode.SingleItem;

        // 高 DPI 屏按物理像素放大请求尺寸，避免系统缩略图被 UniformToFill 拉伸后模糊。
        // GetForCurrentView 仅在拥有激活视图的 UI 线程可用；后台线程（如缓存重建）调用会抛 COMException，
        // 此时回退为 1，退化到原行为，避免整页缩略图加载失败。
        double dpiScale = 1;
        try
        {
            dpiScale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
        }
        catch (Exception)
        {
            dpiScale = 1;
        }

        using var systemThumbnail = await file.GetThumbnailAsync(mode, (uint)(size * dpiScale));

        if (systemThumbnail is not null && systemThumbnail.Size > 0)
        {
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(systemThumbnail);
            return bitmap;
        }

        using var stream = await file.OpenReadAsync();
        var fallback = new BitmapImage
        {
            DecodePixelWidth = size,
            DecodePixelType = DecodePixelType.Logical
        };

        await fallback.SetSourceAsync(stream);
        return fallback;
    }

    private static bool IsVideoFile(string path) =>
        Core.Services.MediaFileClassifier.Classify(path) == Core.Models.MediaKind.Video;
}
