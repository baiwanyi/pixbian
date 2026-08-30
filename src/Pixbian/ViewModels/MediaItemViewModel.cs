/**
 * 图库列表项的视图模型（M2）。
 * 职责：封装单个媒体条目的展示状态，负责按需异步加载缩略图并维护选中与收藏状态。
 * 复用约定：继承 CommunityToolkit.Mvvm 的 ObservableObject，属性变更通过源生成器自动发出通知；
 *          缩略图按需加载，仅在条目进入视口且尺寸变化时触发，避免一次性解码上千张图。
 * 关键约束：IsSelected 由 GridView 的选择机制写入，ViewModel 只做转发，不得反向驱动选择集合，
 *          否则会造成选择与界面的双向绑定环路。
 *          请求缩略图时传显示区高度，由 ResolveDecodeSize 按宽高比换算成显示区最长边；
 *          首帧宽高比尚不可知，故加载完成后若目标尺寸变大再补一次升级加载，
 *          由 _inflightSize 保证同一尺寸的并发请求只执行一次。
 */

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Controls;
using Pixbian.Core.Models;

namespace Pixbian.ViewModels;

/// <summary>图库列表项视图模型。</summary>
public sealed partial class MediaItemViewModel : ObservableObject, IAspectRatioItem
{
    private readonly Func<string, int, CancellationToken, Task<BitmapImage?>> _thumbnailLoader;

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    private int _loadedThumbnailSize;
    private int _requestedThumbnailSize;
    private int _inflightSize;
    private CancellationTokenSource? _loadCts;

    /// <summary>初始化列表项视图模型。</summary>
    /// <param name="item">媒体条目。</param>
    /// <param name="thumbnailLoader">缩略图加载委托。</param>
    public MediaItemViewModel(
        MediaItem item,
        Func<string, int, CancellationToken, Task<BitmapImage?>> thumbnailLoader)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(thumbnailLoader);

        Item = item;
        _thumbnailLoader = thumbnailLoader;
    }

    /// <summary>底层媒体条目。</summary>
    public MediaItem Item { get; }

    /// <summary>主键。</summary>
    public long Id => Item.Id;

    /// <summary>文件名。</summary>
    public string FileName => Item.FileName;

    /// <summary>是否为视频。</summary>
    public bool IsVideo => Item.Kind == MediaKind.Video;

    /// <summary>
    /// 宽高比；优先取已加载缩略图的位图尺寸（索引库多数条目无 Width/Height），
    /// 再回落索引尺寸，均缺失时按方图处理。钳制到合理区间防止布局极端。
    /// </summary>
    public double AspectRatio =>
        GetPixelDimensions() is { Width: > 0, Height: > 0 } d ? FromDimensions(d.Width, d.Height)
        : Item.Width.HasValue && Item.Height.HasValue && Item.Height > 0 ? FromDimensions(Item.Width.Value, Item.Height.Value)
        : 1.0;

    /// <summary>缩略图加载完成或变更后通知依赖此属性的布局面板，并在目标尺寸变大时升级到更清晰的位图。</summary>
    partial void OnThumbnailChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(AspectRatio));

        // 首帧宽高比未知（多为 1.0），位图到位后真实宽高比可能推出更大的目标尺寸，此处补一次升级加载。
        // 只升级不降级：解码舍入会让宽高比在档位边界小幅抖动，允许降级会造成反复重新解码。
        // 置空由调用方主动发起（尺寸或缩放比变化），不在此触发，以免与外部的重加载重复。
        if (value is null || _requestedThumbnailSize <= 0
            || ResolveDecodeSize(_requestedThumbnailSize) <= _loadedThumbnailSize)
        {
            return;
        }

        _ = EnsureThumbnailAsync(_requestedThumbnailSize);
    }

    /// <summary>按像素尺寸计算钳制后的宽高比。</summary>
    private static double FromDimensions(int width, int height) =>
        Math.Clamp((double)width / height, 0.25, 4.0);

    /// <summary>取已加载缩略图的实际位图尺寸。</summary>
    private (int Width, int Height)? GetPixelDimensions()
    {
        if (Thumbnail is not { PixelWidth: > 0, PixelHeight: > 0 })
        {
            return null;
        }

        return (Thumbnail.PixelWidth, Thumbnail.PixelHeight);
    }

    /// <summary>用于分组的日期文本。</summary>
    public string TakenDateText
    {
        get
        {
            // 不使用自定义格式串：中文"年/月/日"若写成 "yyyy年M月d日" 会被格式解析器按字面量处理，
            // 而加单引号转义（"yyyy'年'..."）又会因单引号未配对在运行时抛 FormatException。
            // 用插值直接拼数字，语义清晰且无格式串陷阱。
            var local = (Item.TakenUtc ?? Item.CreatedUtc).ToLocalTime();
            return $"{local.Year}年{local.Month}月{local.Day}日";
        }
    }

    /// <summary>人类可读的文件大小。</summary>
    public string FileSizeText => FormatFileSize(Item.FileSize);

    /// <summary>分辨率文本；未解析时返回空串。</summary>
    public string DimensionText =>
        Item.Width.HasValue && Item.Height.HasValue ? $"{Item.Width} × {Item.Height}" : "—";

    /// <summary>时长文本；图片返回空串。</summary>
    public string DurationText =>
        Item.DurationMs.HasValue ? FormatDuration(TimeSpan.FromMilliseconds(Item.DurationMs.Value)) : string.Empty;

    /// <summary>按需加载缩略图；目标尺寸未变化且已加载、或同尺寸正在加载时跳过。</summary>
    /// <param name="size">显示区高度（逻辑像素），自适应视图即行高。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EnsureThumbnailAsync(int size, CancellationToken cancellationToken = default)
    {
        _requestedThumbnailSize = size;

        var target = ResolveDecodeSize(size);

        if ((_loadedThumbnailSize == target && Thumbnail is not null) || _inflightSize == target)
        {
            return;
        }

        // 取消上一次未完成的加载，避免快速滚动时旧请求覆盖新结果。
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inflightSize = target;

        try
        {
            var bitmap = await _thumbnailLoader(Item.Path, target, _loadCts.Token);

            if (bitmap is not null)
            {
                // 先记录尺寸再赋值：OnThumbnailChanged 以它为基准判断是否要升级到更清晰的位图。
                _loadedThumbnailSize = target;
                Thumbnail = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // 滚动导致的取消属于预期行为。
        }
        finally
        {
            // 已被更高尺寸的升级加载接手时不复位，避免把新请求的在途标记清掉。
            if (_inflightSize == target)
            {
                _inflightSize = 0;
            }
        }
    }

    /// <summary>按显示区最长边推导解码边长；竖图最长边即高度，横图则按宽高比放大，并量化到固定档位。</summary>
    /// <param name="size">显示区高度（逻辑像素）。</param>
    /// <returns>量化后的解码边长。</returns>
    /// <remarks>
    /// 放大上限取 2 倍：常见画幅（4:3 到 2:1）都在上限内可精确匹配显示宽度，
    /// 更宽的画幅（全景图）占比极低，无上限地跟随宽高比会让解码尺寸与内存成倍增长。
    /// </remarks>
    private int ResolveDecodeSize(int size) =>
        ThumbnailSizes.SnapToBucket(size * Math.Clamp(AspectRatio, 1.0, 2.0));

    /// <summary>释放未完成的加载任务。</summary>
    public void CancelPendingLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} B" : $"{value:F1} {units[unitIndex]}";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
