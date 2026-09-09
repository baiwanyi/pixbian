/**
 * 图像编辑服务的集成测试（M3）。
 * 职责：验证裁剪、旋转、调色三类操作的结果正确性，并守护"绝不覆盖原始文件"这一底线约束。
 * 复用约定：用 ImageSharp 现场生成测试图片，不依赖外部素材；每个用例使用独立临时目录。
 * 关键约束：必须保留「输出到源路径时抛异常」用例——这是防止用户原始素材被永久损坏的唯一防线；
 *          裁剪越界时不得抛异常，应钳制到图像边界内，否则界面上的拖拽会频繁崩溃。
 */

using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Pixbian.Imaging.Services;
using Xunit;

namespace Pixbian.Imaging.Tests;

/// <summary>ImageEditService 集成测试。</summary>
public sealed class ImageEditServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ImageEditService _service = new();

    /// <summary>创建临时目录。</summary>
    public ImageEditServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Pixbian.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CropAsync_裁剪指定区域_输出尺寸正确()
    {
        var source = await CreateImageAsync("source.png", 200, 100);
        var destination = Path.Combine(_root, "cropped.png");

        await _service.CropAsync(source, destination, new CropRectangle(10, 20, 50, 40));

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(50, result.Width);
        Assert.Equal(40, result.Height);
    }

    [Fact]
    public async Task CropAsync_区域越界_钳制到边界内而不抛异常()
    {
        var source = await CreateImageAsync("source.png", 100, 100);
        var destination = Path.Combine(_root, "clamped.png");

        await _service.CropAsync(source, destination, new CropRectangle(80, 80, 500, 500));

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(20, result.Width);
        Assert.Equal(20, result.Height);
    }

    [Fact]
    public async Task RotateAsync_顺时针旋转90度_宽高互换()
    {
        var source = await CreateImageAsync("source.png", 120, 80);
        var destination = Path.Combine(_root, "rotated.png");

        await _service.RotateAsync(source, destination, 1);

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(80, result.Width);
        Assert.Equal(120, result.Height);
    }

    [Fact]
    public async Task RotateAsync_旋转180度_尺寸不变()
    {
        var source = await CreateImageAsync("source.png", 120, 80);
        var destination = Path.Combine(_root, "rotated.png");

        await _service.RotateAsync(source, destination, 2);

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(120, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public async Task RotateAsync_圈数超出一圈_按取模结果处理()
    {
        var source = await CreateImageAsync("source.png", 120, 80);
        var destination = Path.Combine(_root, "rotated.png");

        // 5 个四分之一圈等价于 1 个，结果应与顺时针 90 度一致。
        await _service.RotateAsync(source, destination, 5);

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(80, result.Width);
        Assert.Equal(120, result.Height);
    }

    [Fact]
    public async Task RotateAsync_负圈数_按逆时针处理()
    {
        var source = await CreateImageAsync("source.png", 120, 80);
        var destination = Path.Combine(_root, "rotated.png");

        // -1 等价于逆时针 90 度，与顺时针 90 度一样导致宽高互换。
        await _service.RotateAsync(source, destination, -1);

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(80, result.Width);
        Assert.Equal(120, result.Height);
    }

    [Fact]
    public async Task AdjustAsync_调整亮度_输出文件有效()
    {
        var source = await CreateImageAsync("source.png", 60, 60);
        var destination = Path.Combine(_root, "adjusted.png");

        await _service.AdjustAsync(source, destination, new Adjustments(Brightness: 1.3f));

        using var result = Image.Load<Rgba32>(destination);
        Assert.Equal(60, result.Width);
    }

    [Fact]
    public async Task CropAsync_输出路径等同源路径_抛出参数异常()
    {
        var source = await CreateImageAsync("source.png", 100, 100);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CropAsync(source, source, new CropRectangle(0, 0, 10, 10)));
    }

    [Fact]
    public async Task RotateAsync_输出路径等同源路径_抛出参数异常()
    {
        var source = await CreateImageAsync("source.png", 100, 100);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RotateAsync(source, source, 1));
    }

    [Fact]
    public async Task CropAsync_裁剪区域尺寸非正_抛出越界异常()
    {
        var source = await CreateImageAsync("source.png", 100, 100);
        var destination = Path.Combine(_root, "invalid.png");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.CropAsync(source, destination, new CropRectangle(0, 0, 0, 10)));
    }

    [Fact]
    public async Task AdjustAsync_不支持的输出格式_抛出不支持异常()
    {
        var source = await CreateImageAsync("source.png", 60, 60);
        var destination = Path.Combine(_root, "output.tiff");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _service.AdjustAsync(source, destination, new Adjustments()));
    }

    [Fact]
    public async Task CropAsync_编辑完成_源文件保持不变()
    {
        var source = await CreateImageAsync("source.png", 200, 200);
        var before = await File.ReadAllBytesAsync(source);
        var destination = Path.Combine(_root, "cropped.png");

        await _service.CropAsync(source, destination, new CropRectangle(0, 0, 50, 50));

        var after = await File.ReadAllBytesAsync(source);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RotateAsync_源图超过解码上限_抛出不支持异常且不产生输出()
    {
        // 50×50 = 2500 像素；把上限调到 1000 即可模拟「解码炸弹」而不必生成超大图。
        var source = await CreateImageAsync("huge.png", 50, 50);
        var destination = Path.Combine(_root, "out.png");
        _service.MaxDecodedPixels = 1000;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _service.RotateAsync(source, destination, 1));

        Assert.False(File.Exists(destination), "拒绝处理时不得留下任何输出文件。");
    }

    [Fact]
    public async Task RotateAsync_源图在解码上限内_正常处理()
    {
        var source = await CreateImageAsync("normal.png", 50, 50);
        var destination = Path.Combine(_root, "rotated.png");

        // 2500 像素在上限 10000 之内：仅边界校验放行，编辑流程本身不受影响。
        _service.MaxDecodedPixels = 10000;

        await _service.RotateAsync(source, destination, 1);

        Assert.True(File.Exists(destination));
    }

    /// <summary>生成指定尺寸的测试图片并返回路径。</summary>
    private async Task<string> CreateImageAsync(string fileName, int width, int height)
    {
        var path = Path.Combine(_root, fileName);

        using var image = new Image<Rgba32>(width, height);
        image.SaveAsPng(path);

        return await Task.FromResult(path);
    }
}
