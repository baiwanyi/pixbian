/**
 * 路径安全守卫的单元测试。
 * 职责：覆盖目录规范化、穿越阻断、前缀欺骗、大小写与异常路径，守护 CWE-22 防线。
 * 复用约定：全部用例为纯逻辑测试，不触碰真实文件系统特定目录，仅依赖路径字符串运算。
 * 关键约束：前缀欺骗用例（/Lib 与 /Lib2）必须保留，这是本模块最容易回归的缺陷点。
 */

using Pixbian.Core.Utilities;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>PathGuard 测试。</summary>
public sealed class PathGuardTests
{
    [Fact]
    public void NormalizeDirectory_相对路径_返回带尾部分隔符的绝对路径()
    {
        var result = PathGuard.NormalizeDirectory(".");

        Assert.True(Path.IsPathRooted(result));
        Assert.True(Path.EndsInDirectorySeparator(result));
    }

    [Fact]
    public void NormalizeDirectory_已带分隔符_不重复追加()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
        var result = PathGuard.NormalizeDirectory(root);

        Assert.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            result,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            new string(Path.DirectorySeparatorChar, 2),
            result,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeDirectory_空白输入_抛出参数异常(string value)
    {
        Assert.Throws<ArgumentException>(() => PathGuard.NormalizeDirectory(value));
    }

    [Fact]
    public void IsInside_子目录内_返回真()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lib");

        Assert.True(PathGuard.IsInside(root, Path.Combine(root, "sub", "a.jpg")));
    }

    [Fact]
    public void IsInside_使用上层跳转穿越_返回假()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lib");
        var outside = Path.Combine(root, "..", "Windows", "System32", "config");

        Assert.False(PathGuard.IsInside(root, outside));
    }

    [Fact]
    public void IsInside_前缀欺骗目录_返回假()
    {
        // /Lib 不得匹配 /Lib2，否则扫描源边界会被绕过。
        var root = Path.Combine(Path.GetTempPath(), "Lib");
        var sibling = Path.Combine(Path.GetTempPath(), "Lib2", "a.jpg");

        Assert.False(PathGuard.IsInside(root, sibling));
    }

    [Fact]
    public void IsInside_大小写不同_仍判定为内部()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lib");
        var candidate = Path.Combine(root.ToUpperInvariant(), "a.jpg");

        Assert.True(PathGuard.IsInside(root, candidate));
    }

    [Fact]
    public void TryResolveInside_合法子路径_返回解析后的绝对路径()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lib");
        var candidate = Path.Combine(root, "sub", "a.jpg");

        Assert.True(PathGuard.TryResolveInside(root, candidate, out var fullPath));
        Assert.Equal(candidate, fullPath);
    }

    [Fact]
    public void TryResolveInside_越界路径_返回假且路径为空()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lib");
        var outside = Path.Combine(Path.GetTempPath(), "Other", "a.jpg");

        Assert.False(PathGuard.TryResolveInside(root, outside, out var fullPath));
        Assert.Equal(string.Empty, fullPath);
    }

    [Fact]
    public void TryResolveInside_根目录自身_判定为内部()
    {
        // 根目录经规范化后以分隔符结尾，故必须以规范化结果作为输入，而非原始路径。
        var root = PathGuard.NormalizeDirectory(Path.Combine(Path.GetTempPath(), "Lib"));

        Assert.True(PathGuard.TryResolveInside(root, root, out var fullPath));
        Assert.Equal(root, fullPath);
    }

    [Fact]
    public void TryResolveInside_空白输入_抛出参数异常()
    {
        Assert.Throws<ArgumentException>(
            () => PathGuard.TryResolveInside(" ", "a.jpg", out _));
    }
}
