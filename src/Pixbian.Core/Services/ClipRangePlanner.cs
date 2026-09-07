/**
 * 视频片段区间裁决模块。
 * 职责：按「片段区间」设置的档位与视频总时长裁决本次播放的片段，是幻灯片放映与片段界面的唯一出处。
 * 复用约定：纯静态纯函数，不依赖 UI 与 IO；随机数由调用方注入以便单测复现；
 *          区间结果复用 ShortClip（起点 / 终点），与短片播放共用同一表示。
 * 关键约束：选中档位是片段**长度的上限**，实际片段长度在「下限~档位」间随机，
 *          下限为小于该档位的选项中随机一个（选中最小档时无下限，长度固定为该档位）；
 *          起点为随机时间点——时长超过 1 分钟的视频起点不早于 20 秒以避开片头 logo，
 *          更短的视频不限制起点（余量不足起点阈值时退回从头）；
 *          视频总长不超过片段长度时**整段播放**（短视频不截取保护）；
 *          **起点为零且终点等于总长即整段播放语义**，调用方须据此不再额外安排终点计时
 *          （避免与 MediaEnded 双触发造成跳张）。
 */

namespace Pixbian.Core.Services;

/// <summary>视频片段区间裁决器。</summary>
public static class ClipRangePlanner
{
    /// <summary>可选取的档位（秒），即片段长度上限。</summary>
    public static readonly int[] PresetOptions = { 10, 30, 60, 90, 120 };

    /// <summary>需要避开片头的视频时长阈值：超过此值才限制最早起点。</summary>
    public static readonly TimeSpan HeadAvoidThreshold = TimeSpan.FromSeconds(60);

    /// <summary>长视频的最早起点：避开片头 logo。</summary>
    public static readonly TimeSpan HeadAvoidStart = TimeSpan.FromSeconds(20);

    /// <summary>按档位与视频总时长裁决播放片段。</summary>
    /// <param name="duration">视频总时长，须为正。</param>
    /// <param name="presetSeconds">片段长度上限档位（秒），应取 PresetOptions 之一。</param>
    /// <param name="random">随机数生成器，由调用方注入。</param>
    /// <returns>片段区间；起点为零且终点等于总长表示整段播放。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时长不为正时抛出。</exception>
    /// <exception cref="ArgumentNullException">随机数为 null 时抛出。</exception>
    public static ShortClip Plan(TimeSpan duration, int presetSeconds, Random random)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "视频时长必须为正。");
        }

        ArgumentNullException.ThrowIfNull(random);

        var lowerCandidates = PresetOptions.Where(o => o < presetSeconds).ToArray();
        var lowerSeconds = lowerCandidates.Length == 0
            ? presetSeconds
            : lowerCandidates[random.Next(lowerCandidates.Length)];

        var length = TimeSpan.FromSeconds(random.Next(lowerSeconds, presetSeconds + 1));

        // 短视频不截取：装不下这段长度就整段播放。
        if (duration <= length)
        {
            return new ShortClip(TimeSpan.Zero, duration);
        }

        var maxStartSeconds = (int)(duration - length).TotalSeconds;
        var minStartSeconds = duration > HeadAvoidThreshold
            ? (int)HeadAvoidStart.TotalSeconds
            : 0;

        // 余量不足以从阈值起播时退回从头（宁可含片头，也不缩短片段）。
        if (minStartSeconds > maxStartSeconds)
        {
            minStartSeconds = 0;
        }

        var start = TimeSpan.FromSeconds(random.Next(minStartSeconds, maxStartSeconds + 1));

        return new ShortClip(start, start + length);
    }
}
