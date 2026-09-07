/**
 * 幻灯片播放序列器测试。
 * 职责：验证列表序与随机序的推进、边界与洗牌语义。
 * 复用约定：纯逻辑单元测试，不依赖文件系统与数据库。
 * 关键约束：必须保留「随机序一轮完整且起始对齐」用例——
 *          重洗对齐错误会把当前条目重播或漏播；「列表序走完返回 false」
 *          是放映停止的唯一信号，语义错误会导致放映到末尾死循环或提前中断。
 */

using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>幻灯片播放序列器测试。</summary>
public sealed class SlideShowSequencerTests
{
    [Fact]
    public void 列表序_逐张推进且走完返回false()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.List);
        sequencer.Reset(3, 0);

        Assert.True(sequencer.TryAdvance());
        Assert.Equal(1, sequencer.Current);
        Assert.True(sequencer.TryAdvance());
        Assert.Equal(2, sequencer.Current);
        Assert.False(sequencer.TryAdvance());
        Assert.Equal(2, sequencer.Current);
    }

    [Fact]
    public void 列表序_起始索引钳制到有效范围()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.List);
        sequencer.Reset(3, 99);

        Assert.Equal(2, sequencer.Current);
    }

    [Fact]
    public void 随机序_一轮完整且起始条目对齐首位()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Random);
        sequencer.Reset(6, 3);

        Assert.Equal(3, sequencer.Current);

        var visited = new List<int> { 3 };

        for (var i = 0; i < 5; i++)
        {
            Assert.True(sequencer.TryAdvance());
            visited.Add(sequencer.Current);
        }

        Assert.Equal(Enumerable.Range(0, 6), visited.OrderBy(i => i));
    }

    [Fact]
    public void 随机序_一轮走完自动重洗继续放映()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Random);
        sequencer.Reset(4, 0);

        // 连续推进两轮（8 次），每次都应成功，随机序永不停止。
        for (var i = 0; i < 8; i++)
        {
            Assert.True(sequencer.TryAdvance());
        }

        Assert.True(sequencer.Current >= 0 && sequencer.Current < 4);
    }

    [Fact]
    public void 循环序_走完回卷到首张且永不停止()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Loop);
        sequencer.Reset(3, 1);

        Assert.True(sequencer.TryAdvance());
        Assert.Equal(2, sequencer.Current);
        Assert.True(sequencer.TryAdvance());
        Assert.Equal(0, sequencer.Current);
        Assert.True(sequencer.TryAdvance());
        Assert.Equal(1, sequencer.Current);
    }

    [Fact]
    public void 循环序_单条目推进仍指向自身()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Loop);
        sequencer.Reset(1, 0);

        Assert.True(sequencer.TryAdvance());
        Assert.Equal(0, sequencer.Current);
    }

    [Fact]
    public void 循环序_手动跳转越界仍钳制不动()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Loop);
        sequencer.Reset(3, 2);

        Assert.False(sequencer.TryMove(1));
        Assert.Equal(2, sequencer.Current);
    }

    [Fact]
    public void 手动跳转_列表式移动且越界不动()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Random);
        sequencer.Reset(5, 2);

        Assert.True(sequencer.TryMove(-1));
        Assert.Equal(1, sequencer.Current);
        Assert.True(sequencer.TryMove(-1));
        Assert.Equal(0, sequencer.Current);
        Assert.False(sequencer.TryMove(-1));
        Assert.Equal(0, sequencer.Current);
        Assert.True(sequencer.TryMove(4));
        Assert.Equal(4, sequencer.Current);
        Assert.False(sequencer.TryMove(1));
    }

    [Fact]
    public void 手动跳转后_随机推进重洗且一轮完整()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Random);
        sequencer.Reset(5, 0);
        _ = sequencer.TryAdvance();
        Assert.True(sequencer.TryMove(-1));

        var visited = new List<int> { sequencer.Current };

        for (var i = 0; i < 4; i++)
        {
            Assert.True(sequencer.TryAdvance());
            visited.Add(sequencer.Current);
        }

        Assert.Equal(Enumerable.Range(0, 5), visited.OrderBy(i => i));
    }

    [Fact]
    public void 单条目_列表序不可推进()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.List);
        sequencer.Reset(1, 0);

        Assert.False(sequencer.TryAdvance());
    }

    [Fact]
    public void 空列表_推进返回false且游标为0()
    {
        var sequencer = new SlideShowSequencer(SlideShowPlayOrder.Random);
        sequencer.Reset(0, 0);

        Assert.Equal(0, sequencer.Current);
        Assert.False(sequencer.TryAdvance());
    }
}
