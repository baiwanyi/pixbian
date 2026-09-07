/**
 * 视频片段区间裁决测试。
 * 职责：验证「片段区间」档位规则——片段长度落在下限~档位之间、长视频起点不早于 20 秒、
 *      短视频整段播放、最小档固定长度。
 * 复用约定：纯逻辑单元测试，随机数注入固定实例以便复现。
 * 关键约束：必须保留「长视频起点不早于 20 秒」用例（避开片头 logo，用户明确要求）与
 *          「短视频整段播放」用例（缺失会让短视频被截得比原片短得多）。
 */

using System;
using System.Linq;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>视频片段区间裁决测试。</summary>
public sealed class ClipRangePlannerTests
{
    private static readonly TimeSpan LongVideo = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ShortVideo = TimeSpan.FromSeconds(45);

    [Fact]
    public void Plan_视频装不下片段长度_整段播放()
    {
        // 45 秒视频、档位 120（长度至少 10 秒）——45 秒能装下 10~120 的片段，故用 5 秒验证装不下。
        var duration = TimeSpan.FromSeconds(5);

        var clip = ClipRangePlanner.Plan(duration, 10, new Random(42));

        Assert.Equal(TimeSpan.Zero, clip.Start);
        Assert.Equal(duration, clip.End);
    }

    [Fact]
    public void Plan_最小档位_片段长度固定为档位()
    {
        var clip = ClipRangePlanner.Plan(LongVideo, 10, new Random(42));

        Assert.Equal(TimeSpan.FromSeconds(10), clip.Length);
    }

    [Theory]
    [InlineData(30, 10, 30)]
    [InlineData(60, 10, 60)]
    [InlineData(120, 10, 120)]
    public void Plan_长视频_片段长度落在下限至档位之间(int presetSeconds, int minLength, int maxLength)
    {
        var random = new Random(20260907);

        for (var i = 0; i < 300; i++)
        {
            var clip = ClipRangePlanner.Plan(LongVideo, presetSeconds, random);

            Assert.InRange(clip.Length, TimeSpan.FromSeconds(minLength), TimeSpan.FromSeconds(maxLength));
        }
    }

    [Fact]
    public void Plan_长视频_起点不早于20秒且片段不越界()
    {
        var random = new Random(11);

        for (var i = 0; i < 300; i++)
        {
            var clip = ClipRangePlanner.Plan(LongVideo, 120, random);

            Assert.True(
                clip.Start >= ClipRangePlanner.HeadAvoidStart,
                $"起点 {clip.Start} 早于避开片头的最早起点 {ClipRangePlanner.HeadAvoidStart}");
            Assert.True(clip.End <= LongVideo, $"终点 {clip.End} 超出总长 {LongVideo}");
        }
    }

    [Fact]
    public void Plan_一分钟以内视频_起点不受片头限制()
    {
        var random = new Random(3);
        var starts = new HashSet<double>();

        for (var i = 0; i < 200; i++)
        {
            var clip = ClipRangePlanner.Plan(ShortVideo, 10, random);

            Assert.True(clip.End <= ShortVideo, $"终点 {clip.End} 超出总长 {ShortVideo}");
            starts.Add(clip.Start.TotalSeconds);
        }

        // 短视频允许从很靠前的位置起播——此断言守护「1 分钟以内不做片头限制」这条需求。
        Assert.Contains(starts, s => s < ClipRangePlanner.HeadAvoidStart.TotalSeconds);
    }

    [Fact]
    public void Plan_起点存在随机性()
    {
        var random = new Random(7);
        var starts = new HashSet<double>();

        for (var i = 0; i < 100; i++)
        {
            starts.Add(ClipRangePlanner.Plan(LongVideo, 120, random).Start.TotalSeconds);
        }

        Assert.True(starts.Count > 1, "长视频的片段起点应当随机，而非固定在最早起点");
    }

    [Fact]
    public void Plan_片段长度存在随机性()
    {
        var random = new Random(13);
        var lengths = ClipRangePlanner.PresetOptions
            .Select(_ => ClipRangePlanner.Plan(LongVideo, 120, random).Length.TotalSeconds)
            .ToHashSet();

        Assert.True(lengths.Count > 1, "片段长度应当在下限~档位间随机，而非固定为档位");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Plan_时长不为正_抛出参数越界(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClipRangePlanner.Plan(TimeSpan.FromSeconds(seconds), 60, new Random(1)));
    }

    [Fact]
    public void Plan_随机数为空_抛出参数异常()
    {
        Assert.Throws<ArgumentNullException>(
            () => ClipRangePlanner.Plan(TimeSpan.FromSeconds(120), 60, null!));
    }
}
