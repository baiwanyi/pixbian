/**
 * 幻灯片画面合成渲染器。
 * 职责：把「当前照片 + 以它为素材的虚化背景」合成为一张与舞台等大的位图作为放映单帧——
 *      模糊背景铺满全幅、清晰照片按比例居中，与 Windows 照片应用幻灯片的呈现机制一致。
 *      Ken Burns 动画由显示层作用于这张合成图，因合成图始终铺满舞台且缩放倍率恒 ≥ 1，
 *      边缘永远在视口之外，从结构上消除露边与边缘抖动。
 * 复用约定：解码与合成走 Win2D；产物 CanvasImageSource 可直接赋给 Image.Source；
 *          EXIF 方向校正在本类内完成（按角度旋转绘制），显示层不再需要旋转变换。
 * 关键约束：模糊必须先降采样再施加（模糊半径以源图分辨率为准，直接对原图施加几乎无效）；
 *          GaussianBlurEffect 的 BorderMode 必须为 Hard——默认 Soft 会把四周羽化成透明露出黑底；
 *          原图解码后的全尺寸位图内存占用大，绘制完成必须立即释放，
 *          且方向校正位图的最长边有上限以防超大图撑爆内存。
 */

using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pixbian.Services;

/// <summary>把照片与其虚化背景合成为放映单帧的渲染器。</summary>
public sealed class SlideShowFrameRenderer : IDisposable
{
    /// <summary>
    /// 模糊背景的标准差；作用于降采样后的小图。模糊图显示时会放大到舞台宽，
    /// 屏幕上的实际模糊半径约为本值的 2 倍。
    /// </summary>
    private const float BlurAmount = 14f;

    /// <summary>模糊背景相对舞台宽度的降采样比例；模糊图无需全分辨率。</summary>
    private const double BackdropScale = 0.5;

    /// <summary>降采样后模糊背景的最小宽度（逻辑像素）。</summary>
    private const double MinBackdropWidth = 320;

    /// <summary>方向校正位图的最长边上限；超过则等比缩小，控制超大图的性格内存峰值。</summary>
    private const float MaxOrientedSize = 4096f;

    private const float DefaultDpi = 96f;

    private CanvasDevice? _device;

    /// <summary>
    /// 合成一帧放映画面：虚化背景铺满舞台、照片按比例居中。
    /// 必须在 UI 线程调用（产物 CanvasImageSource 是 XAML 对象）。
    /// </summary>
    /// <param name="path">照片文件完整路径。</param>
    /// <param name="viewportWidth">舞台宽度（逻辑像素）。</param>
    /// <param name="viewportHeight">舞台高度（逻辑像素）。</param>
    /// <param name="rotationDegrees">EXIF 显示旋转角（0/90/180/270）。</param>
    /// <param name="blurEnabled">是否启用虚化背景；关闭时背景为纯黑。</param>
    /// <returns>合成帧；解码或渲染失败为 null。</returns>
    public async Task<ImageSource?> CreateFrameAsync(
        string path,
        double viewportWidth,
        double viewportHeight,
        int rotationDegrees,
        bool blurEnabled)
    {
        if (string.IsNullOrEmpty(path) || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return null;
        }

        try
        {
            var device = _device ??= new CanvasDevice();

            using var bitmap = await CanvasBitmap.LoadAsync(device, path).AsTask();
            using var oriented = OrientBitmap(device, bitmap, rotationDegrees);

            var frame = new CanvasImageSource(device, (float)viewportWidth, (float)viewportHeight, DefaultDpi);

            using (var session = frame.CreateDrawingSession(Microsoft.UI.Colors.Black))
            {
                if (blurEnabled)
                {
                    DrawBlurredBackdrop(session, oriented, viewportWidth, viewportHeight);
                }

                DrawCenteredPhoto(session, oriented, viewportWidth, viewportHeight);
            }

            return frame;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or NotSupportedException or UnauthorizedAccessException
                                      or System.IO.IOException)
        {
            Diagnostics.Log($"SLIDESHOW|FRAMFAIL|{ex.GetType().Name}|{ex.HResult}");
            return null;
        }
    }

    /// <summary>把虚化背景以 UniformToFill 铺满整个舞台。</summary>
    private static void DrawBlurredBackdrop(
        CanvasDrawingSession session,
        CanvasRenderTarget oriented,
        double viewportWidth,
        double viewportHeight)
    {
        var backdropWidth = Math.Max(MinBackdropWidth, viewportWidth * BackdropScale);
        var backdropHeight = backdropWidth * viewportHeight / viewportWidth;

        using var scaled = new CanvasRenderTarget(
            session.Device, (float)backdropWidth, (float)backdropHeight, DefaultDpi);

        using (var small = scaled.CreateDrawingSession())
        {
            small.DrawImage(oriented, new Rect(0, 0, backdropWidth, backdropHeight));
        }

        var blur = new GaussianBlurEffect
        {
            Source = scaled,
            BlurAmount = BlurAmount,

            // Hard：边缘按最外圈像素向外延伸；默认 Soft 会把四周羽化成透明露出黑底。
            BorderMode = EffectBorderMode.Hard
        };

        DrawUniformToFill(session, blur, backdropWidth, backdropHeight, viewportWidth, viewportHeight);
    }

    /// <summary>把照片以 Uniform 居中绘制到舞台；高质量三次插值保证缩小后的清晰度。</summary>
    private static void DrawCenteredPhoto(
        CanvasDrawingSession session,
        CanvasRenderTarget oriented,
        double viewportWidth,
        double viewportHeight)
    {
        var size = oriented.Size;
        var aspect = size.Width / size.Height;
        var photoWidth = Math.Min(viewportWidth, viewportHeight * aspect);
        var photoHeight = photoWidth / aspect;

        var destination = new Rect(
            (viewportWidth - photoWidth) / 2, (viewportHeight - photoHeight) / 2, photoWidth, photoHeight);

        session.DrawImage(
            oriented,
            destination,
            new Rect(0, 0, size.Width, size.Height),
            1f,
            CanvasImageInterpolation.HighQualityCubic);
    }

    /// <summary>把源图以 UniformToFill 绘制到目标区域：按目标宽高比取源图居中区域拉伸。</summary>
    private static void DrawUniformToFill(
        CanvasDrawingSession session,
        ICanvasImage image,
        double imageWidth,
        double imageHeight,
        double destWidth,
        double destHeight)
    {
        var imageAspect = imageWidth / imageHeight;
        var destAspect = destWidth / destHeight;

        Rect source;
        if (imageAspect > destAspect)
        {
            var width = imageHeight * destAspect;
            source = new Rect((imageWidth - width) / 2, 0, width, imageHeight);
        }
        else
        {
            var height = imageWidth / destAspect;
            source = new Rect(0, (imageHeight - height) / 2, imageWidth, height);
        }

        session.DrawImage(image, new Rect(0, 0, destWidth, destHeight), source);
    }

    /// <summary>
    /// 按 EXIF 角度把原图旋转为显示方向的位图；最长边超限时等比缩小控制内存峰值。
    /// 旋转矩阵按「先旋转、后平移到目标包围盒原点」组合（Y 向下坐标系，正角为顺时针）。
    /// </summary>
    private static CanvasRenderTarget OrientBitmap(CanvasDevice device, CanvasBitmap bitmap, int rotationDegrees)
    {
        var pixels = bitmap.SizeInPixels;
        var shrink = Math.Min(1f, MaxOrientedSize / Math.Max(pixels.Width, pixels.Height));
        var width = pixels.Width * shrink;
        var height = pixels.Height * shrink;
        var quarter = rotationDegrees % 180 != 0;

        var target = new CanvasRenderTarget(
            device, (float)(quarter ? height : width), (float)(quarter ? width : height), DefaultDpi);

        using var session = target.CreateDrawingSession();
        session.Clear(Microsoft.UI.Colors.Transparent);

        if (rotationDegrees % 360 == 0)
        {
            session.DrawImage(bitmap, new Rect(0, 0, width, height));
            return target;
        }

        var rotation = Matrix3x2.CreateRotation((float)(rotationDegrees * Math.PI / 180.0));
        var translation = rotationDegrees switch
        {
            90 => Matrix3x2.CreateTranslation(height, 0),
            180 => Matrix3x2.CreateTranslation(width, height),
            _ => Matrix3x2.CreateTranslation(0, width)
        };

        session.Transform = Matrix3x2.CreateScale(shrink) * rotation * translation;
        session.DrawImage(bitmap);

        return target;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
    }
}
