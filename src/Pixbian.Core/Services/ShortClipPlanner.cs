/**
 * 短片片段规划模块。
 * 职责：按视频总时长裁决短片页本次要播放的片段区间，是短片播放策略的唯一出处。
 * 复用约定：纯静态纯函数，不依赖 UI 与 IO；随机数由调用方注入以便单测复现。
 * 关键约束：60 秒是硬边界而非近似值——不超过 60 秒整段播放，超过才截片段；
 *           长视频片段起点不得早于 20 秒以避开片头，长度在 40~80 秒间随机；
 *           总长不足 100 秒（20 + 80）时退化为「20 秒到片尾」，即「不够 80 秒按 20 秒以后的最长计算」。
 */

namespace Pixbian.Core.Services;

/// <summary>短片播放片段。</summary>
/// <param name="Start">片段起点。</param>
/// <param name="End">片段终点；不超过视频总长。</param>
public readonly record struct ShortClip(TimeSpan Start, TimeSpan End)
{
    /// <summary>片段时长。</summary>
    public TimeSpan Length => End - Start;
}

/// <summary>短片片段规划器。</summary>
public static class ShortClipPlanner
{
    /// <summary>整段播放的时长上限：不超过此值的视频从头播到尾。</summary>
    public static readonly TimeSpan FullPlaybackLimit = TimeSpan.FromSeconds(60);

    /// <summary>长视频片段的最小长度。</summary>
    public static readonly TimeSpan MinClipLength = TimeSpan.FromSeconds(40);

    /// <summary>长视频片段的最大长度。</summary>
    public static readonly TimeSpan MaxClipLength = TimeSpan.FromSeconds(80);

    /// <summary>长视频片段的最早起点。</summary>
    public static readonly TimeSpan MinClipStart = TimeSpan.FromSeconds(20);

    /// <summary>按视频总时长规划本次播放的片段。</summary>
    /// <param name="duration">视频总时长，须为正。</param>
    /// <param name="random">随机数生成器，由调用方注入。</param>
    /// <returns>片段区间。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时长不为正时抛出。</exception>
    /// <exception cref="ArgumentNullException">随机数为 null 时抛出。</exception>
    public static ShortClip Plan(TimeSpan duration, Random random)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "视频时长必须为正。");
        }

        ArgumentNullException.ThrowIfNull(random);

        // ① 不超过 60 秒：整段播放。
        if (duration <= FullPlaybackLimit)
        {
            return new ShortClip(TimeSpan.Zero, duration);
        }

        // ② 60 至 100 秒：20 秒之后到片尾不足 80 秒，按「20 秒以后的最长」截到片尾。
        if (duration < MinClipStart + MaxClipLength)
        {
            return new ShortClip(MinClipStart, duration);
        }

        // ③ 100 秒及以上：长度在 40~80 秒间随机，起点在 20 秒与「片尾前留足该长度」之间随机。
        var length = MinClipLength
            + TimeSpan.FromSeconds(random.NextDouble() * (MaxClipLength - MinClipLength).TotalSeconds);

        var start = MinClipStart
            + TimeSpan.FromSeconds(random.NextDouble() * (duration - length - MinClipStart).TotalSeconds);

        return new ShortClip(start, start + length);
    }
}
