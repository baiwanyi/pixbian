/**
 * 短片片段规划的单元测试。
 * 职责：覆盖 60 秒分档边界、长视频片段的长度与起点区间，以及入参校验。
 * 复用约定：随机分支用固定种子构造随机数并循环取样，保证断言可复现又能覆盖取值区间。
 * 关键约束：60 秒与 100 秒两个边界用例必须保留——前者是「整段播放」与「截取片段」的分界，
 *           后者是「20 秒以后最长」与「40~80 秒随机片段」的分界，改动算法时最易在此回归。
 */

using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>ShortClipPlanner 测试。</summary>
public sealed class ShortClipPlannerTests
{
    /// <summary>浮点累加可能让终点比总长大出极小量（纳秒级），断言留 1 毫秒容差。</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(59)]
    [InlineData(60)]
    public void Plan_不超过60秒_整段播放(int seconds)
    {
        var clip = ShortClipPlanner.Plan(TimeSpan.FromSeconds(seconds), new Random(42));

        Assert.Equal(TimeSpan.Zero, clip.Start);
        Assert.Equal(TimeSpan.FromSeconds(seconds), clip.End);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(75)]
    [InlineData(99)]
    public void Plan_60至100秒_自20秒截到片尾(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        var clip = ShortClipPlanner.Plan(duration, new Random(42));

        Assert.Equal(ShortClipPlanner.MinClipStart, clip.Start);
        Assert.Equal(duration, clip.End);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(180)]
    [InlineData(600)]
    [InlineData(3600)]
    public void Plan_100秒及以上_长度落在40至80秒(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        var random = new Random(20260906);

        for (var i = 0; i < 500; i++)
        {
            var clip = ShortClipPlanner.Plan(duration, random);

            Assert.InRange(clip.Length, ShortClipPlanner.MinClipLength, ShortClipPlanner.MaxClipLength);
        }
    }

    [Fact]
    public void Plan_100秒及以上_起点不早于20秒()
    {
        var random = new Random(7);

        for (var i = 0; i < 500; i++)
        {
            var clip = ShortClipPlanner.Plan(TimeSpan.FromMinutes(5), random);

            Assert.True(
                clip.Start >= ShortClipPlanner.MinClipStart,
                $"起点 {clip.Start} 早于最早起点 {ShortClipPlanner.MinClipStart}");
        }
    }

    [Fact]
    public void Plan_终点不超过视频总长()
    {
        var duration = TimeSpan.FromSeconds(150);
        var random = new Random(11);

        for (var i = 0; i < 500; i++)
        {
            var clip = ShortClipPlanner.Plan(duration, random);

            Assert.True(clip.End <= duration + Tolerance, $"终点 {clip.End} 超出总长 {duration}");
        }
    }

    [Fact]
    public void Plan_长视频_起点存在随机性()
    {
        var random = new Random(99);
        var starts = new HashSet<double>();

        for (var i = 0; i < 100; i++)
        {
            starts.Add(ShortClipPlanner.Plan(TimeSpan.FromMinutes(30), random).Start.TotalSeconds);
        }

        // 起点若被写死为 20，集合只会有一个元素——此断言守护「20 秒以后随机截取」这条需求。
        Assert.True(starts.Count > 1, "长视频的片段起点应当随机，而非固定为最早起点");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Plan_时长不为正_抛出参数越界(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ShortClipPlanner.Plan(TimeSpan.FromSeconds(seconds), new Random(1)));
    }

    [Fact]
    public void Plan_随机数为空_抛出参数异常()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShortClipPlanner.Plan(TimeSpan.FromSeconds(30), null!));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(600)]
    public void Plan_自定义长度范围_长度落在自定义区间(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        var random = new Random(20260907);

        for (var i = 0; i < 500; i++)
        {
            var clip = ShortClipPlanner.Plan(
                duration,
                random,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60));

            Assert.InRange(clip.Length, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void Plan_自定义最大长度小于最小长度_按最小长度钳制()
    {
        var duration = TimeSpan.FromMinutes(5);

        var clip = ShortClipPlanner.Plan(
            duration,
            new Random(42),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(20));

        Assert.InRange(clip.Length, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Plan_自定义范围_总长不足20秒加最大长度_截到片尾()
    {
        // 79 秒：20 + 最大 60 = 80 > 79，20 秒之后到片尾不足最大长度，应截到片尾。
        var clip = ShortClipPlanner.Plan(
            TimeSpan.FromSeconds(79),
            new Random(42),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(20), clip.Start);
        Assert.Equal(TimeSpan.FromSeconds(79), clip.End);
    }
}
