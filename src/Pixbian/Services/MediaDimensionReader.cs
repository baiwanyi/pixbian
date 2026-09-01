/**
 * 媒体文件头读取器：只读文件头取得显示尺寸与时长。
 * 职责：为缩略图服务的按需探测与后台元数据回填提供同一份实现，避免两处各写一遍并各自漂移。
 * 复用约定：图片用 BitmapDecoder 的 OrientedPixel*（已计入 EXIF 方向），与缩略图解码结果完全一致；
 *          视频容器 WIC 不支持，必须改读系统视频属性并按旋转标记还原宽高，不能与图片共用路径。
 * 关键约束：只读文件头、绝不解码像素——这是它远快于缩略图解码的唯一原因，任何改动都不得违反；
 *          读取失败一律返回 null，由调用方决定回退策略，绝不允许把异常抛到界面层。
 */

using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Pixbian.Services;

/// <summary>媒体文件头的尺寸与时长读取器。</summary>
internal static class MediaDimensionReader
{
    /// <summary>读取媒体文件的显示尺寸与时长。</summary>
    /// <param name="file">媒体文件。</param>
    /// <param name="isVideo">是否为视频。</param>
    /// <returns>尺寸与时长；读取失败或格式不受支持时为 null。</returns>
    internal static async Task<(int Width, int Height, long? DurationMs)?> ReadAsync(
        StorageFile file,
        bool isVideo) =>
        isVideo ? await ReadVideoAsync(file) : await ReadImageAsync(file);

    /// <summary>图片尺寸：取 OrientedPixel*（已计入 EXIF 方向）；图片无时长概念。</summary>
    /// <param name="file">图片文件。</param>
    private static async Task<(int Width, int Height, long? DurationMs)?> ReadImageAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();

        if (stream is null || stream.Size == 0)
        {
            return null;
        }

        // BitmapDecoder 只读文件头即可给出尺寸，不解码像素，故远快于解码缩略图。
        var decoder = await BitmapDecoder.CreateAsync(stream);

        return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, null);
    }

    /// <summary>视频尺寸与时长：读系统视频属性并按旋转标记还原宽高。</summary>
    /// <param name="file">视频文件。</param>
    private static async Task<(int Width, int Height, long? DurationMs)?> ReadVideoAsync(StorageFile file)
    {
        var properties = await file.Properties.GetVideoPropertiesAsync();

        if (properties.Width is not > 0 || properties.Height is not > 0)
        {
            return null;
        }

        var width = (int)properties.Width;
        var height = (int)properties.Height;

        var durationMs = properties.Duration > TimeSpan.Zero
            ? (long)Math.Round(properties.Duration.TotalMilliseconds)
            : (long?)null;

        // 手机竖拍视频的帧数据横向存储，靠旋转标记还原为竖屏；不处理旋转会让宽高比反过来，
        // 比不探测更糟。180 度不改变宽高比，无需交换。
        return properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270
            ? (height, width, durationMs)
            : (width, height, durationMs);
    }
}
