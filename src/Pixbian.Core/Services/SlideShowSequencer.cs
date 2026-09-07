/**
 * 幻灯片播放序列器。
 * 职责：维护幻灯片放映的条目游标，支持列表顺序与随机洗牌两种推进方式，
 *      供放映驱动方按「是否推进成功」决定继续放映或停止。
 * 复用约定：纯索引逻辑，不感知媒体类型与播放列表内容，仅以条目计数参与运算；
 *          列表序走完返回 false 由调用方决定停止，随机序一轮走完自动重洗循环放映。
 * 关键约束：随机序列以当前条目为首位对齐，重洗后推进取的是序列第二位，
 *          否则会把当前条目重播一遍；手动跳转后游标与序列错位，
 *          下次推进前必须以当前条目重洗，否则随机游标与实际显示条目不一致。
 */

using Pixbian.Core.Models;

namespace Pixbian.Core.Services;

/// <summary>幻灯片播放序列器：推进游标与洗牌，不感知媒体内容。</summary>
public sealed class SlideShowSequencer
{
    private readonly SlideShowPlayOrder _order;

    private int _count;
    private int _current;
    private int[] _shuffleOrder = [];
    private int _shuffleCursor;

    /// <summary>初始化序列器；播放顺序在构造时固定，运行期变更顺序由调用方重建实例或重新 Reset。</summary>
    /// <param name="order">播放顺序。</param>
    public SlideShowSequencer(SlideShowPlayOrder order)
    {
        _order = order;
    }

    /// <summary>当前条目索引；未装载时为 0。</summary>
    public int Current => _current;

    /// <summary>装载序列：以指定条目计数与起始索引重新开始，清空既有的随机序列。</summary>
    /// <param name="count">条目总数；0 表示空列表。</param>
    /// <param name="startIndex">起始索引，自动钳制到有效范围。</param>
    public void Reset(int count, int startIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        _count = count;
        _current = count == 0 ? 0 : Math.Clamp(startIndex, 0, count - 1);
        _shuffleOrder = [];
        _shuffleCursor = 0;
    }

    /// <summary>以当前条目为首位重新洗牌；放映起播与运行期变更播放顺序时调用。</summary>
    public void Reshuffle()
    {
        var indices = Enumerable.Range(0, _count).ToArray();
        Random.Shared.Shuffle(indices);

        var position = Array.IndexOf(indices, _current);

        if (position > 0)
        {
            (indices[0], indices[position]) = (indices[position], indices[0]);
        }

        _shuffleOrder = indices;
        _shuffleCursor = 0;
    }

    /// <summary>放映自动推进到下一张；随机序一轮播完自动以当前条目重洗进入下一轮，列表序走完返回 false。</summary>
    /// <returns>是否成功推进；列表序无下一张时为 false。</returns>
    public bool TryAdvance()
    {
        if (_count == 0)
        {
            return false;
        }

        if (_order == SlideShowPlayOrder.List)
        {
            if (_current + 1 >= _count)
            {
                return false;
            }

            _current++;
            return true;
        }

        if (!IsShuffleCursorValid() || _shuffleCursor + 1 >= _shuffleOrder.Length)
        {
            Reshuffle();
        }

        _shuffleCursor++;
        _current = _shuffleOrder[_shuffleCursor];
        return true;
    }

    /// <summary>手动跳转：按相对偏移做列表式相邻移动（随机模式下亦然），并使随机序列失效。</summary>
    /// <param name="offset">相对偏移，通常为 1（下一张）或 -1（上一张）。</param>
    /// <returns>是否越界移动成功；目标越界时游标不动并返回 false。</returns>
    public bool TryMove(int offset)
    {
        var target = _current + offset;

        if (_count == 0 || target < 0 || target >= _count)
        {
            return false;
        }

        _current = target;
        _shuffleOrder = [];
        _shuffleCursor = 0;
        return true;
    }

    /// <summary>游标是否仍与当前条目对得上：手动跳转后序列即失效，须重洗。</summary>
    private bool IsShuffleCursorValid() =>
        _shuffleCursor < _shuffleOrder.Length && _shuffleOrder[_shuffleCursor] == _current;
}
