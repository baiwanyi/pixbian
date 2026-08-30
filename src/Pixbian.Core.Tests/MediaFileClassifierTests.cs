/**
 * 媒体文件分类器的单元测试。
 * 职责：覆盖图片、视频、不支持格式、大小写与无扩展名等判定分支。
 * 复用约定：直接调用静态分类器，不依赖文件系统。
 * 关键约束：HEIC 与 MKV 用例必须保留，二者分别代表「依赖系统扩展的图片」与「容器格式视频」两条关键路径。
 */

using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>MediaFileClassifier 测试。</summary>
public sealed class MediaFileClassifierTests
{
    [Theory]
    [InlineData("a.jpg")]
    [InlineData("a.jpeg")]
    [InlineData("a.png")]
    [InlineData("a.gif")]
    [InlineData("a.webp")]
    [InlineData("a.bmp")]
    [InlineData("a.tiff")]
    public void Classify_常见图片格式_返回图片(string fileName)
    {
        Assert.Equal(MediaKind.Image, MediaFileClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("a.heic")]
    [InlineData("a.heif")]
    [InlineData("a.avif")]
    [InlineData("a.arw")]
    [InlineData("a.dng")]
    public void Classify_高动态与原始格式_返回图片(string fileName)
    {
        Assert.Equal(MediaKind.Image, MediaFileClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("a.mp4")]
    [InlineData("a.mov")]
    [InlineData("a.avi")]
    [InlineData("a.mkv")]
    [InlineData("a.wmv")]
    [InlineData("a.webm")]
    public void Classify_常见视频格式_返回视频(string fileName)
    {
        Assert.Equal(MediaKind.Video, MediaFileClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("PHOTO.JPG")]
    [InlineData("Clip.MKV")]
    public void Classify_扩展名大写_忽略大小写正确判定(string fileName)
    {
        Assert.NotEqual(MediaKind.Other, MediaFileClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("readme.md")]
    [InlineData("noextension")]
    public void Classify_非媒体文件_返回其他(string fileName)
    {
        Assert.Equal(MediaKind.Other, MediaFileClassifier.Classify(fileName));
    }

    [Fact]
    public void Classify_完整路径_仅依据扩展名判定()
    {
        Assert.Equal(
            MediaKind.Image,
            MediaFileClassifier.Classify(Path.Combine("D:", "Photos", "2026", "IMG_0001.jpg")));
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.mkv", true)]
    [InlineData("a.txt", false)]
    [InlineData("noextension", false)]
    public void IsSupported_与分类结果一致(string fileName, bool expected)
    {
        Assert.Equal(expected, MediaFileClassifier.IsSupported(fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_空白输入_抛出参数异常(string fileName)
    {
        Assert.Throws<ArgumentException>(() => MediaFileClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSupported_空白输入_返回假而不抛出(string fileName)
    {
        // 监控事件回调中不便处理异常，故 IsSupported 对无效输入返回 false。
        Assert.False(MediaFileClassifier.IsSupported(fileName));
    }
}
