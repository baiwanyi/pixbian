/**
 * 条目视图模型（MediaItemViewModel）纯展示逻辑的单元测试。
 * 职责：锁定收藏态回写、宽高比取值优先级（预取 > 索引 > 位图 > 方图兜底）与
 *       钳制、尺寸通知去重、解码尺寸解析（显示区最长边优先），以及
 *       缩略图加载状态机的可离线分支（失败 / 取消 / 同尺寸在途跳过）。
 * 复用约定：loader 恒返 null 或挂起（不创建 BitmapImage，规避 UI 亲和），
 *          以 loader 收到的参数与调用计数作为观察点；
 *          状态机的位图就绪分支（Loaded 升级链路）不在本层覆盖。
 * 关键约束：AspectRatio 的「预取 > 索引」顺序是布局循环（LayoutCycle）事故的防线，
 *          相关取值顺序用例不得删减或反转。
 */

using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Controls;
using Pixbian.ViewModels;
using Xunit;

namespace Pixbian.UI.Tests.ViewModels;

/// <summary>MediaItemViewModel 纯展示逻辑测试。</summary>
public sealed class MediaItemViewModelTests
{
    private static MediaItemViewModel CreateItem(MediaItem? item = null) => new(
        item ?? new MediaItem { Id = 1, Path = "D:\\Lib\\a.jpg", FileName = "a.jpg", Kind = MediaKind.Image },
        (_, _, _) => Task.FromResult<BitmapImage?>(null));

    [Fact]
    public void SetFavorite_回写Item与字形()
    {
        var item = CreateItem();

        item.SetFavorite(true);

        Assert.True(item.IsFavorite);
        Assert.True(item.Item.IsFavorite);
        Assert.Equal("\uEB52", item.FavoriteGlyph);

        item.SetFavorite(false);

        Assert.False(item.Item.IsFavorite);
        Assert.Equal("\uEB51", item.FavoriteGlyph);
    }

    [Fact]
    public void AspectRatio_无任何尺寸来源_方图兜底()
    {
        var item = CreateItem();

        Assert.Equal(1.0, item.AspectRatio);
    }

    [Fact]
    public void AspectRatio_预取尺寸_优先于索引尺寸()
    {
        var item = CreateItem(new MediaItem
        {
            Id = 1,
            Path = "D:\\Lib\\a.jpg",
            FileName = "a.jpg",
            Width = 1000,
            Height = 1000
        });

        // 预取尺寸（来自文件头探测）与索引尺寸不一致时必须信预取：它计入 EXIF 方向，更准确。
        item.SetDimensions(1600, 900);

        Assert.Equal(16.0 / 9.0, item.AspectRatio, precision: 4);
    }

    [Fact]
    public void AspectRatio_仅索引尺寸_按索引取值()
    {
        var item = CreateItem(new MediaItem
        {
            Id = 1,
            Path = "D:\\Lib\\a.jpg",
            FileName = "a.jpg",
            Width = 1600,
            Height = 900
        });

        Assert.Equal(16.0 / 9.0, item.AspectRatio, precision: 4);
    }

    [Fact]
    public void AspectRatio_极端比例_钳制到上限()
    {
        var item = CreateItem();

        item.SetDimensions(1000, 100);

        // 10:1 超出上限，钳制到 4.0，防止布局出现极端条目。
        Assert.Equal(4.0, item.AspectRatio, precision: 4);
    }

    [Fact]
    public void SetDimensions_相同值重复写入_不再发尺寸通知()
    {
        var item = CreateItem();
        var notifications = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaItemViewModel.AspectRatio))
            {
                notifications++;
            }
        };

        item.SetDimensions(1600, 900);
        item.SetDimensions(1600, 900);

        // 首次通知必发（哨兵 NaN），重复写同值静默——否则布局面板空转重测。
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_解码尺寸取显示区最长边()
    {
        int? requestedSize = null;
        var item = new MediaItemViewModel(
            new MediaItem
            {
                Id = 1,
                Path = "D:\\Lib\\a.jpg",
                FileName = "a.jpg",
                Kind = MediaKind.Image
            },
            (_, size, _) =>
            {
                requestedSize = size;
                return Task.FromResult<BitmapImage?>(null);
            });

        item.SetDimensions(1600, 900);
        item.SetDisplaySize(200, 100);

        await item.EnsureThumbnailAsync(256);

        // 布局已回写显示尺寸时按最长边取解码边长，而非按宽高比估算的名义行高。
        Assert.Equal(200, requestedSize);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_loader返回null_状态置为失败()
    {
        var item = CreateItem();

        await item.EnsureThumbnailAsync(256);

        Assert.Equal(ThumbnailLoadState.Failed, item.ThumbnailState);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_取消异常_状态保持加载中而非失败()
    {
        var gate = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new MediaItemViewModel(
            new MediaItem { Id = 1, Path = "D:\\Lib\\a.jpg", FileName = "a.jpg", Kind = MediaKind.Image },
            (_, _, token) =>
            {
                token.Register(() => gate.TrySetCanceled(token));
                return gate.Task;
            });

        var loading = item.EnsureThumbnailAsync(256);

        // 滚动取消（外部取消 loader 的令牌）属于预期行为：EnsureThumbnailAsync 在内部
        // 吞掉 OperationCanceledException（调用方无需处理），且状态不得置为 Failed，
        // 否则一滚动就满屏错误占位；滚回时按需加载会重新请求。
        item.CancelPendingLoad();

        await loading;
        Assert.NotEqual(ThumbnailLoadState.Failed, item.ThumbnailState);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_同尺寸在途_不重复发起()
    {
        var loadCount = 0;
        var gate = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new MediaItemViewModel(
            new MediaItem { Id = 1, Path = "D:\\Lib\\a.jpg", FileName = "a.jpg", Kind = MediaKind.Image },
            (_, _, _) =>
            {
                loadCount++;
                return gate.Task;
            });

        var first = item.EnsureThumbnailAsync(256);
        await item.EnsureThumbnailAsync(256);

        // 同尺寸在途时第二次调用必须直接跳过：否则滚动抖动会打爆解码信号量。
        Assert.Equal(1, loadCount);
        gate.TrySetResult(null);
        await first;
    }
}
