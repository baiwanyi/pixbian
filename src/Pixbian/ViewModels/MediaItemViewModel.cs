/**
 * 图库列表项的视图模型（M2）。
 * 职责：封装单个媒体条目的展示状态，负责按需异步加载缩略图并维护选中与收藏状态。
 * 复用约定：继承 CommunityToolkit.Mvvm 的 ObservableObject，属性变更通过源生成器自动发出通知；
 *          缩略图按需加载，仅在条目进入视口且尺寸变化时触发，避免一次性解码上千张图。
 * 关键约束：IsSelected 由 GridView 的选择机制写入，ViewModel 只做转发，不得反向驱动选择集合，
 *          否则会造成选择与界面的双向绑定环路。
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

    /// <summary>缩略图加载完成或变更后通知依赖此属性的布局面板。</summary>
    partial void OnThumbnailChanged(BitmapImage? value) => OnPropertyChanged(nameof(AspectRatio));

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

    /// <summary>按需加载缩略图；尺寸未变化且已加载时跳过。</summary>
    /// <param name="size">目标边长（像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EnsureThumbnailAsync(int size, CancellationToken cancellationToken = default)
    {
        if (_loadedThumbnailSize == size && Thumbnail is not null)
        {
            return;
        }

        // 取消上一次未完成的加载，避免快速滚动时旧请求覆盖新结果。
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var bitmap = await _thumbnailLoader(Item.Path, size, _loadCts.Token);

            if (bitmap is not null)
            {
                Thumbnail = bitmap;
                _loadedThumbnailSize = size;
            }
        }
        catch (OperationCanceledException)
        {
            // 滚动导致的取消属于预期行为。
        }
    }

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
