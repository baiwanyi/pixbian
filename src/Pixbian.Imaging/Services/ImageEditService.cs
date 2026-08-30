/**
 * 图像编辑服务（M3）。
 * 职责：提供非破坏性的裁剪、旋转与基础调色，输出始终写入新文件。
 * 复用约定：编辑管线统一基于 ImageSharp 的 Mutate；旋转使用 RotateMode 枚举做 90 度整数倍旋转，
 *          任意角度旋转交由界面层的渲染变换处理，不在此落盘。
 * 关键约束：严禁覆盖原始文件——任何编辑都必须输出到新路径，原图保持只读，
 *          这是防止用户误操作导致原始素材永久损坏的底线；
 *          解码时必须显式设置 Rgba32 且不做自动方向校正，由调用方按 EXIF Orientation 统一处理，
 *          避免"解码端已旋转，界面又旋转一次"的双重旋转缺陷。
 */

using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Pixbian.Imaging.Services;

/// <summary>裁剪区域（像素坐标，相对原图）。</summary>
/// <param name="X">左上角横坐标。</param>
/// <param name="Y">左上角纵坐标。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public sealed record CropRectangle(int X, int Y, int Width, int Height);

/// <summary>基础调色参数。</summary>
/// <param name="Brightness">亮度，1.0 为原始值。</param>
/// <param name="Contrast">对比度，1.0 为原始值。</param>
/// <param name="Saturation">饱和度，1.0 为原始值。</param>
public sealed record Adjustments(float Brightness = 1f, float Contrast = 1f, float Saturation = 1f);

/// <summary>图像编辑服务。</summary>
public interface IImageEditService
{
    /// <summary>按区域裁剪并保存到新文件。</summary>
    /// <param name="sourcePath">源文件路径，保持只读。</param>
    /// <param name="destinationPath">输出文件路径，不得与源路径相同。</param>
    /// <param name="crop">裁剪区域。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CropAsync(
        string sourcePath,
        string destinationPath,
        CropRectangle crop,
        CancellationToken cancellationToken = default);

    /// <summary>按 90 度的整数倍旋转并保存到新文件。</summary>
    /// <param name="sourcePath">源文件路径，保持只读。</param>
    /// <param name="destinationPath">输出文件路径，不得与源路径相同。</param>
    /// <param name="quarterTurns">顺时针旋转的四分之一圈数，可为负数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RotateAsync(
        string sourcePath,
        string destinationPath,
        int quarterTurns,
        CancellationToken cancellationToken = default);

    /// <summary>应用调色参数并保存到新文件。</summary>
    /// <param name="sourcePath">源文件路径，保持只读。</param>
    /// <param name="destinationPath">输出文件路径，不得与源路径相同。</param>
    /// <param name="adjustments">调色参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task AdjustAsync(
        string sourcePath,
        string destinationPath,
        Adjustments adjustments,
        CancellationToken cancellationToken = default);
}

/// <summary>基于 ImageSharp 的图像编辑服务。</summary>
public sealed class ImageEditService : IImageEditService
{
    /// <inheritdoc />
    public Task CropAsync(
        string sourcePath,
        string destinationPath,
        CropRectangle crop,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(crop);
        ValidateDestination(sourcePath, destinationPath);

        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "裁剪区域的宽高必须为正数。");
        }

        return ProcessAsync(sourcePath, destinationPath, image =>
        {
            // 裁剪区域裁剪到图像边界内，避免越界导致解码器抛异常。
            var x = Math.Clamp(crop.X, 0, image.Width - 1);
            var y = Math.Clamp(crop.Y, 0, image.Height - 1);
            var width = Math.Clamp(crop.Width, 1, image.Width - x);
            var height = Math.Clamp(crop.Height, 1, image.Height - y);

            image.Mutate(context => context.Crop(new Rectangle(x, y, width, height)));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task RotateAsync(
        string sourcePath,
        string destinationPath,
        int quarterTurns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateDestination(sourcePath, destinationPath);

        var turns = ((quarterTurns % 4) + 4) % 4;
        var mode = turns switch
        {
            1 => RotateMode.Rotate90,
            2 => RotateMode.Rotate180,
            3 => RotateMode.Rotate270,
            _ => RotateMode.None
        };

        if (mode == RotateMode.None)
        {
            return Task.CompletedTask;
        }

        return ProcessAsync(
            sourcePath,
            destinationPath,
            image => image.Mutate(context => context.Rotate(mode)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task AdjustAsync(
        string sourcePath,
        string destinationPath,
        Adjustments adjustments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(adjustments);
        ValidateDestination(sourcePath, destinationPath);

        return ProcessAsync(sourcePath, destinationPath, image => image.Mutate(context =>
        {
            context.Brightness(adjustments.Brightness);
            context.Contrast(adjustments.Contrast);
            context.Saturate(adjustments.Saturation);
        }), cancellationToken);
    }

    /// <summary>在后台线程执行解码、变换与编码，避免阻塞 UI。</summary>
    private static async Task ProcessAsync(
        string sourcePath,
        string destinationPath,
        Action<Image<Rgba32>> transform,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var image = Image.Load<Rgba32>(sourcePath);
            transform(image);

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 照片编辑结果仅输出到 JPEG 与 PNG：前者用于照片（体积小），后者用于需要无损的场景。
            // 其余格式（如 HEIC、WebP）的编码依赖与许可情况各异，暂不提供写回能力。
            var extension = Path.GetExtension(destinationPath);

            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                image.SaveAsJpeg(destinationPath);
                return;
            }

            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                image.SaveAsPng(destinationPath);
                return;
            }

            throw new NotSupportedException(
                $"不支持输出到 {extension} 格式，编辑结果请保存为 JPG 或 PNG。");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>禁止输出到源路径，防止覆盖原始素材。</summary>
    private static void ValidateDestination(string sourcePath, string destinationPath)
    {
        if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "输出路径不得与源路径相同，编辑操作必须保留原始文件。",
                nameof(destinationPath));
        }
    }
}
