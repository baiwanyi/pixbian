/**
 * 短片片段规划模块（历史规划器，当前无调用方）。
 * 职责：按视频总时长裁决本次要播放的片段区间，规则为「起点不早于 20 秒、长度 40~80 秒随机」。
 *          当前幻灯片放映与片段界面的片段裁决统一走 ClipRangePlanner（按「片段区间」档位），
 *          本模块保留为备用规划器，勿在两处并存造成策略漂移。
 * 复用约定：纯静态纯函数，不依赖 UI 与 IO；随机数由调用方注入以便单测复现。
 * 关键约束：60 秒是硬边界而非近似值——不超过 60 秒整段播放，超过才截片段；
 *           长视频片段起点不得早于 20 秒以避开片头，长度默认在 40~80 秒间随机，
 *           调用方可传入自定义长度范围（幻灯片的「片段区间」设置），范围会被钳制为 min ≤ max；
 *           总长不足「20 秒 + 最大长度」时退化为「20 秒到片尾」，即「不够最长按 20 秒以后的最长计算」。
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
    /// <param name="clipMinLength">片段最小长度；缺省用默认值（40 秒）。</param>
    /// <param name="clipMaxLength">片段最大长度；缺省用默认值（80 秒），小于最小长度时按最小长度计。</param>
    /// <returns>片段区间。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时长不为正时抛出。</exception>
    /// <exception cref="ArgumentNullException">随机数为 null 时抛出。</exception>
    public static ShortClip Plan(
        TimeSpan duration,
        Random random,
        TimeSpan? clipMinLength = null,
        TimeSpan? clipMaxLength = null)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "视频时长必须为正。");
        }

        ArgumentNullException.ThrowIfNull(random);

        var minLength = clipMinLength ?? MinClipLength;
        var maxLength = clipMaxLength ?? MaxClipLength;

        // 防御性钳制：最大长度不得低于最小长度。
        if (maxLength < minLength)
        {
            maxLength = minLength;
        }

        // ① 不超过 60 秒：整段播放。
        if (duration <= FullPlaybackLimit)
        {
            return new ShortClip(TimeSpan.Zero, duration);
        }

        // ② 60 秒至「20 秒 + 最大长度」：20 秒之后到片尾不足最大长度，按「20 秒以后的最长」截到片尾。
        if (duration < MinClipStart + maxLength)
        {
            return new ShortClip(MinClipStart, duration);
        }

        // ③ 「20 秒 + 最大长度」及以上：长度在最小~最大之间随机，起点在 20 秒与「片尾前留足该长度」之间随机。
        var length = minLength
            + TimeSpan.FromSeconds(random.NextDouble() * (maxLength - minLength).TotalSeconds);

        var start = MinClipStart
            + TimeSpan.FromSeconds(random.NextDouble() * (duration - length - MinClipStart).TotalSeconds);

        return new ShortClip(start, start + length);
    }
}
