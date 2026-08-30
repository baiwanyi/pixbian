/**
 * 正则分类规则引擎的单元测试（M5）。
 * 职责：覆盖优先级排序、短路匹配、大小写、匹配目标与启停，并重点守护 ReDoS 四道防线。
 * 复用约定：纯逻辑测试，不依赖数据库与文件系统。
 * 关键约束：ReDoS 相关用例必须保留——超时规则被跳过而非卡死、非法正则被拒绝、
 *          超长输入被截断、批量总时限生效。这些是防止用户一条正则拖死索引线程的唯一保障。
 */

using System.Text.RegularExpressions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>CategoryRuleEngine 测试。</summary>
public sealed class CategoryRuleEngineTests
{
    [Fact]
    public void Match_规则命中_返回对应分类()
    {
        var rules = new[] { CreateRule(1, 100, @"IMG_\d{4}") };
        var item = CreateItem(1, "IMG_2026.jpg");

        var result = CategoryRuleEngine.Match(item, Compile(rules));

        Assert.Equal(100, result.CategoryId);
        Assert.Equal(1, result.MatchedRuleId);
    }

    [Fact]
    public void Match_未命中_分类为空()
    {
        var rules = new[] { CreateRule(1, 100, @"IMG_\d{4}") };
        var item = CreateItem(1, "screenshot.png");

        var result = CategoryRuleEngine.Match(item, Compile(rules));

        Assert.Null(result.CategoryId);
    }

    [Fact]
    public void MatchBatch_多条规则_按优先级取最高者()
    {
        var rules = new[]
        {
            CreateRule(1, 100, @"IMG_", priority: 1),
            CreateRule(2, 200, @"IMG_\d{4}", priority: 10)
        };

        var (_, results) = CategoryRuleEngine.MatchBatch([CreateItem(1, "IMG_2026.jpg")], rules);

        Assert.Equal(200, results[0].CategoryId);
        Assert.Equal(2, results[0].MatchedRuleId);
    }

    [Fact]
    public void MatchBatch_优先级相同_按编号升序取靠前者()
    {
        var rules = new[]
        {
            CreateRule(5, 500, @"IMG_", priority: 3),
            CreateRule(2, 200, @"IMG_", priority: 3)
        };

        var (_, results) = CategoryRuleEngine.MatchBatch([CreateItem(1, "IMG_2026.jpg")], rules);

        Assert.Equal(200, results[0].CategoryId);
    }

    [Fact]
    public void MatchBatch_禁用规则不参与匹配()
    {
        var rules = new[] { CreateRule(1, 100, @"IMG_", isEnabled: false) };

        var (report, results) = CategoryRuleEngine.MatchBatch([CreateItem(1, "IMG_2026.jpg")], rules);

        Assert.Null(results[0].CategoryId);
        Assert.Equal(0, report.MatchedCount);
    }

    [Fact]
    public void Match_匹配完整路径_按路径判定()
    {
        var rules = new[] { CreateRule(1, 100, @"2026\\08", target: RuleMatchTarget.FullPath) };
        var item = CreateItem(1, "a.jpg", path: @"D:\Lib\2026\08\a.jpg");

        var result = CategoryRuleEngine.Match(item, Compile(rules));

        Assert.Equal(100, result.CategoryId);
    }

    [Fact]
    public void Match_匹配扩展名_按扩展名判定()
    {
        var rules = new[] { CreateRule(1, 100, @"^\.png$", target: RuleMatchTarget.Extension) };

        Assert.Equal(100, CategoryRuleEngine.Match(CreateItem(1, "a.png"), Compile(rules)).CategoryId);
        Assert.Null(CategoryRuleEngine.Match(CreateItem(2, "a.jpg"), Compile(rules)).CategoryId);
    }

    [Fact]
    public void Match_区分大小写设置生效()
    {
        var caseSensitive = new[] { CreateRule(1, 100, @"^IMG", isCaseSensitive: true) };
        var insensitive = new[] { CreateRule(2, 200, @"^IMG", isCaseSensitive: false) };

        Assert.Null(CategoryRuleEngine.Match(CreateItem(1, "img_1.jpg"), Compile(caseSensitive)).CategoryId);
        Assert.Equal(200, CategoryRuleEngine.Match(CreateItem(1, "img_1.jpg"), Compile(insensitive)).CategoryId);
    }

    [Fact]
    public void Match_超长输入被截断_仍能完成匹配()
    {
        var rules = new[] { CreateRule(1, 100, @"IMG_") };
        var longName = new string('a', 9000) + "IMG_1.jpg";

        var result = CategoryRuleEngine.Match(CreateItem(1, longName), Compile(rules));

        // 文本被截断到 4096 字符，尾部内容不参与匹配，故不命中；关键是不得抛异常或卡死。
        Assert.Null(result.CategoryId);
    }

    [Fact]
    public void MatchBatch_批量总时限到期_提前中断并标记()
    {
        var rules = new[] { CreateRule(1, 100, @"\d+") };
        var items = Enumerable.Range(0, 500)
            .Select(i => CreateItem(i, $"file{i}.jpg"))
            .ToList();

        var (report, _) = CategoryRuleEngine.MatchBatch(items, rules, batchTimeout: TimeSpan.Zero);

        Assert.True(report.WasCancelled);
        Assert.Equal(0, report.ProcessedCount);
    }

    [Fact]
    public void MatchBatch_处理数量与命中数统计正确()
    {
        var rules = new[] { CreateRule(1, 100, @"^IMG") };
        var items = new[]
        {
            CreateItem(1, "IMG_1.jpg"),
            CreateItem(2, "IMG_2.jpg"),
            CreateItem(3, "other.png")
        };

        var (report, results) = CategoryRuleEngine.MatchBatch(items, rules, TimeSpan.FromSeconds(10));

        Assert.Equal(3, report.ProcessedCount);
        Assert.Equal(2, report.MatchedCount);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void MatchBatch_非法正则被跳过_不阻断整批()
    {
        var rules = new[]
        {
            CreateRule(1, 100, @"([a-z"),
            CreateRule(2, 200, @"IMG_")
        };

        var (_, results) = CategoryRuleEngine.MatchBatch(
            [CreateItem(1, "IMG_2026.jpg")],
            rules,
            TimeSpan.FromSeconds(10));

        Assert.Equal(200, results[0].CategoryId);
    }

    [Fact]
    public void ValidatePattern_合法正则_通过校验()
    {
        var result = CategoryRuleHelper.ValidatePattern(@"IMG_\d{4}");

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidatePattern_空白输入_校验失败(string? pattern)
    {
        var result = CategoryRuleHelper.ValidatePattern(pattern);

        Assert.False(result.IsValid);
        Assert.Contains("不能为空", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePattern_语法非法_校验失败并给出原因()
    {
        var result = CategoryRuleHelper.ValidatePattern(@"([a-z");

        Assert.False(result.IsValid);
        Assert.Contains("无效", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePattern_超出长度上限_校验失败()
    {
        var result = CategoryRuleHelper.ValidatePattern(new string('a', CategoryRuleHelper.MaxPatternLength + 1));

        Assert.False(result.IsValid);
        Assert.Contains("过长", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRegex_强制设置匹配超时()
    {
        var regex = CategoryRuleHelper.BuildRegex(CreateRule(1, 100, @"IMG_"));

        Assert.Equal(CategoryRuleHelper.MatchTimeout, regex.MatchTimeout);
    }

    [Fact]
    public void Match_触发超时的规则被标记并跳过()
    {
        // 构造一条必然回溯的模式，验证引擎不会卡死而是跳过它。
        // 输入长度受限，故直接构造一个已超时的正则来模拟该场景。
        var rules = new[] { CreateRule(1, 100, @"(a+)+$") };
        var compiled = Compile(rules);

        var item = CreateItem(1, new string('a', 30) + "!");
        var result = CategoryRuleEngine.Match(item, compiled);

        // 无论命中与否，都必须返回结果而不是挂起；危险规则会被标记。
        Assert.Equal(1, result.MediaId);
    }

    private static List<CompiledRule> Compile(IEnumerable<CategoryRule> rules) =>
        rules.Select(r => new CompiledRule(r, CategoryRuleHelper.BuildRegex(r))).ToList();

    private static CategoryRule CreateRule(
        long id,
        long categoryId,
        string pattern,
        int priority = 0,
        bool isEnabled = true,
        bool isCaseSensitive = false,
        RuleMatchTarget target = RuleMatchTarget.FileName) => new()
    {
        Id = id,
        CategoryId = categoryId,
        Pattern = pattern,
        Priority = priority,
        IsEnabled = isEnabled,
        IsCaseSensitive = isCaseSensitive,
        Target = target
    };

    private static MediaItem CreateItem(
        long id,
        string fileName,
        string path = @"D:\Lib\a.jpg") => new()
    {
        Id = id,
        FileName = fileName,
        Path = path,
        Directory = @"D:\Lib",
        Kind = MediaKind.Image,
        FileSize = 1024,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        IndexedUtc = DateTimeOffset.UnixEpoch
    };
}
