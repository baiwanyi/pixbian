/**
 * 备份文件名规则的单元测试。
 * 职责：锁定两种备份文件名的构造与反解——规范命名必须能取出时间戳，
 *      非规范命名（旧版命名、缺段、被加了后缀）必须返回 null。
 * 复用约定：时间戳一律构造为字符串传入，不依赖真实时钟。
 * 关键约束：反解是「成组清理」的唯一依据，过于宽松会把用户手工文件误删，
 *          过于严格会让备份文件永远清不掉，两侧都必须锁住。
 */

using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>备份文件名规则测试。</summary>
public sealed class BackupFileNamingTests
{
    [Fact]
    public void BuildUserDataFileName_符合约定的命名()
    {
        Assert.Equal(
            "backup-pixbian-userdata-20260910-194105.json",
            BackupFileNaming.BuildUserDataFileName("20260910-194105"));
    }

    [Fact]
    public void BuildIndexFileName_符合约定的命名()
    {
        Assert.Equal(
            "backup-pixbian-index-20260910-194105.db",
            BackupFileNaming.BuildIndexFileName("20260910-194105"));
    }

    [Fact]
    public void BuildBaseName_不含扩展名_供文件选择器预填()
    {
        Assert.Equal(
            "backup-pixbian-userdata-20260910-194105",
            BackupFileNaming.BuildUserDataBaseName("20260910-194105"));
        Assert.Equal(
            "backup-pixbian-index-20260910-194105",
            BackupFileNaming.BuildIndexBaseName("20260910-194105"));
    }

    [Theory]
    [InlineData("backup-pixbian-userdata-20260910-194105.json", "20260910-194105")]
    [InlineData("backup-pixbian-index-20260910-194105.db", "20260910-194105")]
    [InlineData("BACKUP-PIXBIAN-INDEX-20260910-194105.DB", "20260910-194105")]
    public void TryGetStamp_规范命名取出时间戳(string fileName, string expected)
    {
        Assert.Equal(expected, BackupFileNaming.TryGetStamp(fileName));
    }

    [Theory]
    [InlineData("Pixbian-userdata-20260910-194105.json")]
    [InlineData("backup-pixbian-userdata-latest.json")]
    [InlineData("backup-pixbian-index-2026091-194105.db")]
    [InlineData("backup-pixbian-userdata-2026091a-194105.json")]
    [InlineData("backup-pixbian-userdata-20260910-194105.json.bak")]
    [InlineData("backup-pixbian-userdata-20260910-194105.txt")]
    [InlineData("notes.txt")]
    public void TryGetStamp_非规范命名返回null(string fileName)
    {
        Assert.Null(BackupFileNaming.TryGetStamp(fileName));
    }
}
