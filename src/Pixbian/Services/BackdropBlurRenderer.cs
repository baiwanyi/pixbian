/**
 * 幻灯片背景虚化渲染器。
 * 职责：把图片解码后降采样并经高斯模糊，产出可直接赋给 Image.Source 的模糊位图，
 *      用作放映舞台上「当前照片的虚化放大」背景。
 * 复用约定：解码与模糊走 Win2D（XAML 无内置模糊，缩略图管线的降采样放大只能得到
 *          「糊掉的色块」而非毛玻璃）；产物是 CanvasImageSource，可直接进 Image.Source。
 * 关键约束：必须先降采样再模糊——模糊半径以源图分辨率为准，对全尺寸原图施加同样的
 *          半径几乎看不出效果，而先缩到舞台量级再模糊才能得到毛玻璃质感；
 *          解码出的全尺寸位图内存占用大，绘制完成后必须立即 Dispose；
 *          CanvasImageSource 与 CanvasDevice 均为 GPU 资源，前者随 Image.Source 替换由 GC 回收，
 *          后者由本类持有并在 Dispose 释放，否则反复放映会累积设备资源。
 */

using System;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pixbian.Services;

/// <summary>把图片渲染为高斯模糊背景位图的渲染器。</summary>
public sealed class BackdropBlurRenderer : IDisposable
{
    /// <summary>
    /// 高斯模糊的标准差；越大越糊，3 倍标准差之外的像素基本不受影响。
    /// 注意模糊图显示时还要再放大到舞台宽（SourceScale 的倒数倍），屏幕上的实际模糊半径约为本值的 2 倍。
    /// </summary>
    private const float BlurAmount = 8f;

    /// <summary>模糊位图相对舞台宽度的比例尺：模糊图无需全分辨率，降采样可大幅省内存与耗时。</summary>
    private const double SourceScale = 0.5;

    /// <summary>降采样后的最小宽度（逻辑像素），避免小窗口下背景过于粗糙。</summary>
    private const double MinSourceWidth = 320;

    private const float DefaultDpi = 96f;

    /// <summary>Win2D 设备；懒创建（GPU 资源，构造成本不低）。</summary>
    private CanvasDevice? _device;

    /// <summary>模糊背景的渲染产物。</summary>
    /// <param name="Source">可直接赋给 Image.Source 的模糊位图。</param>
    /// <param name="AspectRatio">显示宽高比（已按显示旋转换算），供宿主按舞台宽推算高度。</param>
    public sealed record BlurBackdrop(ImageSource Source, double AspectRatio);

    /// <summary>
    /// 生成指定图片的模糊背景位图；解码或渲染失败时返回 null，调用方退化为纯黑背景。
    /// 必须在 UI 线程调用：产物 CanvasImageSource 是 XAML 对象。
    /// </summary>
    /// <param name="path">图片文件完整路径。</param>
    /// <param name="viewportWidth">舞台宽度（逻辑像素）；决定模糊图的降采样尺寸。</param>
    /// <param name="rotationDegrees">显示旋转角度；90 / 270 度时宽高比取反。</param>
    /// <returns>模糊背景与其显示宽高比；失败为 null。</returns>
    public async Task<BlurBackdrop?> CreateBlurSourceAsync(string path, double viewportWidth, int rotationDegrees)
    {
        // 舞台尚未布局时宽度可能为 0，此时按最小宽度兜底，不因此放弃渲染。
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var device = _device ??= new CanvasDevice();

            // 解码在线程池完成，回到 UI 线程后创建 XAML 位图源并绘制。
            using var bitmap = await CanvasBitmap.LoadAsync(device, path).AsTask();

            var aspect = GetDisplayAspect(bitmap, rotationDegrees);
            var width = Math.Max(MinSourceWidth, viewportWidth * SourceScale);
            var height = width / aspect;

            // 先降采样：模糊半径作用于此尺寸，才能得到可见的毛玻璃效果。
            using var scaled = new CanvasRenderTarget(device, (float)width, (float)height, DefaultDpi);

            using (var session = scaled.CreateDrawingSession())
            {
                session.DrawImage(bitmap, new Rect(0, 0, width, height));
            }

            var source = new CanvasImageSource(device, (float)width, (float)height, DefaultDpi);

            using (var session = source.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                session.DrawImage(new GaussianBlurEffect
                {
                    Source = scaled,
                    BlurAmount = BlurAmount,

                    // Hard：边缘按最外圈像素向外延伸。默认的 Soft 会把边缘羽化成透明，
                    // 露出舞台黑底，观感就是背景左右各有一条渐隐到黑的渐变带。
                    BorderMode = EffectBorderMode.Hard
                });
            }

            return new BlurBackdrop(source, aspect);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or NotSupportedException or UnauthorizedAccessException
                                      or System.IO.IOException)
        {
            Diagnostics.Log($"SLIDESHOW|BLURFAIL|{ex.GetType().Name}|{ex.HResult}");
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
    }

    /// <summary>按显示旋转换算图片的显示宽高比；旋转 90 / 270 度时宽高互换。</summary>
    /// <param name="bitmap">已解码的图片。</param>
    /// <param name="rotationDegrees">显示旋转角度。</param>
    private static double GetDisplayAspect(CanvasBitmap bitmap, int rotationDegrees)
    {
        var pixels = bitmap.SizeInPixels;
        var isQuarterTurn = rotationDegrees % 180 != 0;
        var width = isQuarterTurn ? pixels.Height : pixels.Width;
        var height = isQuarterTurn ? pixels.Width : pixels.Height;

        return height == 0 ? 1d : (double)width / height;
    }
}
