/**
 * 应用日志写入器的单元测试。
 * 职责：验证脱敏规则与日志滚动归档行为，防止日志成为隐私外泄面或磁盘占用源。
 * 复用约定：滚动用例使用临时目录与调小的阈值，不触碰用户真实日志目录；
 *          IP 与路径脱敏为纯函数，直接断言输出形态。
 * 关键约束：脱敏用例必须覆盖「非法输入」——外部传入的 IP 与路径均不可信，
 *          解析失败必须退化为占位符而非抛出，否则日志写入会变成新的故障源。
 */

using Pixbian.Core.Utilities;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>AppLog 测试。</summary>
public sealed class AppLogTests
{
    [Fact]
    public void RedactIp_IPv4地址_抹去主机位只留网段()
    {
        Assert.Equal("192.168.1.0/24", AppLog.RedactIp("192.168.1.123"));
    }

    [Fact]
    public void RedactIp_IPv6地址_只保留前缀段()
    {
        var redacted = AppLog.RedactIp("2001:0db8:85a3:0000:0000:8a2e:0370:7334");

        // IPAddress 解析后输出规范形式（前导零被去掉），故期望值为 2001:db8 而非原始写法。
        Assert.StartsWith("2001:db8", redacted, StringComparison.Ordinal);
        Assert.EndsWith("/32", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("7334", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData(null)]
    public void RedactIp_非法输入_退化为占位符而不抛出(string? value)
    {
        Assert.Equal("-", AppLog.RedactIp(value));
    }

    [Fact]
    public void RedactPath_完整路径_只保留文件名()
    {
        Assert.Equal("photo.jpg", AppLog.RedactPath(Path.Combine("D:", "Photos", "2026", "photo.jpg")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RedactPath_空输入_退化为占位符(string? value)
    {
        Assert.Equal("-", AppLog.RedactPath(value));
    }

    [Fact]
    public void RotateIfNeeded_超过阈值_滚动为归档文件()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-log-");

        try
        {
            var path = Path.Combine(directory.FullName, AppLog.FileName);
            File.WriteAllText(path, new string('x', 128));

            AppLog.MaxFileSizeBytes = 64;
            AppLog.RotateIfNeeded(path);

            Assert.True(File.Exists($"{path}.1"), "超限日志应滚动为 .1 归档。");
            Assert.False(File.Exists(path), "滚动后当前日志文件应让位给新写入。");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RotateIfNeeded_未达阈值_保持原文件不动()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-log-");

        try
        {
            var path = Path.Combine(directory.FullName, AppLog.FileName);
            File.WriteAllText(path, new string('x', 8));

            AppLog.MaxFileSizeBytes = 64;
            AppLog.RotateIfNeeded(path);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists($"{path}.1"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RotateIfNeeded_滚动到归档上限_最旧一份被覆盖()
    {
        var directory = Directory.CreateTempSubdirectory("pixbian-log-");

        try
        {
            var path = Path.Combine(directory.FullName, AppLog.FileName);
            File.WriteAllText(path, new string('a', 128));

            AppLog.MaxFileSizeBytes = 64;

            // 连续三轮：顺序应为 .1 → .2 → .3，第四份无处可去即被覆盖，总份数不再增长。
            AppLog.RotateIfNeeded(path);
            File.WriteAllText(path, new string('b', 128));
            AppLog.RotateIfNeeded(path);
            File.WriteAllText(path, new string('c', 128));
            AppLog.RotateIfNeeded(path);
            File.WriteAllText(path, new string('d', 128));
            AppLog.RotateIfNeeded(path);

            Assert.True(File.Exists($"{path}.1"));
            Assert.True(File.Exists($"{path}.2"));
            Assert.True(File.Exists($"{path}.3"));
            Assert.False(File.Exists($"{path}.4"), "归档份数必须封顶，否则日志会无限增长。");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
