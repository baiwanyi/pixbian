/**
 * 图库列表项的视图模型（M2）。
 * 职责：封装单个媒体条目的展示状态，负责按需异步加载缩略图并维护选中与收藏状态。
 * 复用约定：继承 CommunityToolkit.Mvvm 的 ObservableObject，属性变更通过源生成器自动发出通知；
 *          缩略图按需加载，仅在条目进入视口且尺寸变化时触发，避免一次性解码上千张图。
 * 关键约束：IsSelected 由 GridView 的选择机制写入，ViewModel 只做转发，不得反向驱动选择集合，
 *          否则会造成选择与界面的双向绑定环路。
 *          解码边长取自布局面板回写的实际显示尺寸（IDisplaySizeAware），未回写时按显示区高度
 *          乘宽高比估算；尺寸只升不降且升幅须超过容差，否则解码舍入与布局抖动会让条目反复重新解码。
 *          宽高比与分辨率均由外部预取写入（SetDimensions），但二者取值来源不同：分辨率忌用
 *          缩略图位图（降采样后的值），详见 DimensionText 的说明。
 *          缩略图有加载中/已加载/失败三态（ThumbnailState），由界面据此在骨架屏与错误占位间切换；
 *          取消**不属于失败**，滚出视口的取消须回落为加载中，否则界面会随机冒出错误占位。
 */

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Controls;
using Pixbian.Core.Models;

namespace Pixbian.ViewModels;

/// <summary>图库列表项视图模型。</summary>
public sealed partial class MediaItemViewModel : ObservableObject, IAspectRatioItem, IDisplaySizeAware
{
    /// <summary>升级加载的容差：目标边长须比已加载边长大出该比例才重新解码。</summary>
    private const double UpgradeTolerance = 1.12;

    /// <summary>显示尺寸容差（逻辑像素）：布局抖动在该幅度内视为不变。</summary>
    private const double DisplaySizeTolerance = 1.0;

    private readonly Func<string, int, CancellationToken, Task<BitmapImage?>> _thumbnailLoader;

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    private ThumbnailLoadState _thumbnailState;

    [ObservableProperty]
    private bool _isSelected;

    private double _displayWidth;
    private double _displayHeight;
    private int? _probeWidth;
    private int? _probeHeight;
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

    /// <summary>宽高比；按位图尺寸、预取尺寸、索引尺寸的顺序取值，均缺失时按方图处理。</summary>
    /// <remarks>
    /// 预取尺寸先于位图到位，使布局在缩略图解码完成前就按真实比例排列，
    /// 否则每个条目都要先从方图跳到真实比例、整行跟着重排。钳制到合理区间防止布局极端。
    /// </remarks>
    public double AspectRatio
    {
        get
        {
            if (GetPixelDimensions() is { Width: > 0, Height: > 0 } bitmap)
            {
                return FromDimensions(bitmap.Width, bitmap.Height);
            }

            if (_probeWidth is > 0 && _probeHeight is > 0)
            {
                return FromDimensions(_probeWidth.Value, _probeHeight.Value);
            }

            if (Item.Width is > 0 && Item.Height is > 0)
            {
                return FromDimensions(Item.Width.Value, Item.Height.Value);
            }

            return 1.0;
        }
    }

    /// <summary>写入预取到的媒体尺寸，使布局在缩略图到位前就能按真实宽高比排列。</summary>
    /// <param name="width">像素宽度（已计入 EXIF 方向与视频旋转）。</param>
    /// <param name="height">像素高度（已计入 EXIF 方向与视频旋转）。</param>
    /// <remarks>须同时通知 AspectRatio 与 DimensionText：预取是异步到达的，若只通知前者，
    /// 已绑定分辨率的界面（右键菜单读取时虽为主动取值，但任何 XAML 绑定都依赖此通知）不会刷新。</remarks>
    public void SetDimensions(int width, int height)
    {
        if (_probeWidth == width && _probeHeight == height)
        {
            return;
        }

        _probeWidth = width;
        _probeHeight = height;
        OnPropertyChanged(nameof(AspectRatio));
        OnPropertyChanged(nameof(DimensionText));
    }

    /// <summary>接收布局面板回写的实际显示尺寸，并据此请求更匹配的位图。</summary>
    /// <param name="width">显示宽度（逻辑像素）。</param>
    /// <param name="height">显示高度（逻辑像素）。</param>
    public void SetDisplaySize(double width, double height)
    {
        if (Math.Abs(width - _displayWidth) < DisplaySizeTolerance
            && Math.Abs(height - _displayHeight) < DisplaySizeTolerance)
        {
            return;
        }

        _displayWidth = width;
        _displayHeight = height;

        // 尚未进入加载流程的条目不做处理，稍后由 EnsureThumbnailAsync 统一发起。
        if (_requestedThumbnailSize > 0)
        {
            _ = EnsureThumbnailAsync(_requestedThumbnailSize);
        }
    }

    /// <summary>缩略图加载完成或变更后通知依赖此属性的布局面板。</summary>
    partial void OnThumbnailChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(AspectRatio));

        // 位图被外部置空（切换缩略图尺寸或显示缩放比）时回到骨架屏，
        // 使下一次请求必定重新解码且期间不残留上一张图。
        if (value is null)
        {
            _loadedThumbnailSize = 0;
            ThumbnailState = ThumbnailLoadState.Loading;
            return;
        }

        ThumbnailState = ThumbnailLoadState.Loaded;

        // 首帧布局尚未回写显示尺寸，到位后由此补一次升级加载；
        // 当前尺寸是否已满足由 EnsureThumbnailAsync 统一判断。
        if (_requestedThumbnailSize > 0)
        {
            _ = EnsureThumbnailAsync(_requestedThumbnailSize);
        }
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

    /// <summary>原图分辨率文本；未解析时返回破折号。</summary>
    /// <remarks>扫描器不写入 MediaItem 的宽高（避免首次扫描从秒级掉到分钟级），分辨率经
    /// GetDimensionsAsync 预取后由 SetDimensions 落到 _probeWidth/_probeHeight，故此处只按
    ///「预取尺寸 > 索引字段」取值。
    /// 注意与 AspectRatio 的优先级**刻意不同**：Thumbnail 是按显示区降采样解码的位图
    /// （档位 128~2560），其像素是缩略图大小而非原图分辨率。宽高比是相对值、用位图兜底无害，
    /// 分辨率是绝对像素、用位图会直接给出错误数值，故此处绝不读取 Thumbnail。</remarks>
    public string DimensionText
    {
        get
        {
            if (_probeWidth is > 0 && _probeHeight is > 0)
            {
                return $"{_probeWidth.Value} × {_probeHeight.Value}";
            }

            if (Item.Width is > 0 && Item.Height is > 0)
            {
                return $"{Item.Width.Value} × {Item.Height.Value}";
            }

            return "—";
        }
    }

    /// <summary>时长文本；图片返回空串。</summary>
    public string DurationText =>
        Item.DurationMs.HasValue ? FormatDuration(TimeSpan.FromMilliseconds(Item.DurationMs.Value)) : string.Empty;

    /// <summary>按需加载缩略图；目标尺寸未变大、或同尺寸正在加载时跳过。</summary>
    /// <param name="size">显示区高度（逻辑像素），自适应视图即名义行高、网格视图即格子边长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EnsureThumbnailAsync(int size, CancellationToken cancellationToken = default)
    {
        _requestedThumbnailSize = size;

        var target = ResolveDecodeSize();

        // 只升不降，且升幅须超过容差：解码舍入与布局抖动会让目标尺寸小幅上下浮动，
        // 无条件跟随会造成反复重新解码。置空由调用方主动发起，此处不处理。
        if (_inflightSize == target
            || (Thumbnail is not null && target <= _loadedThumbnailSize * UpgradeTolerance))
        {
            return;
        }

        // 取消上一次未完成的加载，避免快速滚动时旧请求覆盖新结果。
        // 只取消不释放：下游解码任务仍持有旧 Token，立即释放会在其取消传播路径上
        // 抛 ObjectDisposedException 炸断 WhenAll 提交链；CTS 无内核句柄，交由 GC 回收。
        _loadCts?.Cancel();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inflightSize = target;

        // 仅在尚无位图可显示时才退回骨架屏。
        // 已有位图时（升级加载、显示尺寸变化后的重解码）必须保持图片可见：
        // 否则刚淡入完成的图片会被打回骨架、加载完再淡入一次，观感即为闪烁。
        // 失败重试不在此列——失败时 Thumbnail 为 null，仍会正确回到骨架屏。
        if (Thumbnail is null)
        {
            ThumbnailState = ThumbnailLoadState.Loading;
        }

        try
        {
            var bitmap = await _thumbnailLoader(Item.Path, target, _loadCts.Token);

            if (bitmap is not null)
            {
                // 先记录尺寸再赋值：OnThumbnailChanged 以它为基准判断是否要升级到更清晰的位图。
                _loadedThumbnailSize = target;
                Thumbnail = bitmap;
            }
            else
            {
                // 服务层已吞掉具体异常（文件丢失、占用、格式不受支持），此处只区分最终结果。
                ThumbnailState = ThumbnailLoadState.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            // 滚动导致的取消属于预期行为，维持 Loading 而非 Failed，
            // 否则一滚动就会满屏错误占位；滚回时按需加载会重新请求。
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

    /// <summary>取显示区最长边作为解码边长；布局尚未回写尺寸时按宽高比估算。</summary>
    /// <returns>显示区最长边（逻辑像素）。</returns>
    /// <remarks>
    /// 估算时的放大上限取 2 倍：常见画幅（4:3 到 2:1）都在上限内可精确匹配显示宽度，
    /// 更宽的画幅（全景图）占比极低，无上限地跟随宽高比会让解码尺寸与内存成倍增长。
    /// 量化到固定档位由缩略图服务在物理像素域完成，此处保持逻辑值以便做尺寸比较。
    /// </remarks>
    private int ResolveDecodeSize()
    {
        var longest = Math.Max(_displayWidth, _displayHeight);

        // 首帧尚未完成布局，以显示区高度按宽高比估算最长边。
        if (longest <= 0)
        {
            longest = _requestedThumbnailSize * Math.Clamp(AspectRatio, 1.0, 2.0);
        }

        return (int)Math.Ceiling(longest);
    }

    /// <summary>取消未完成的加载任务；在条目移出集合（切换文件夹/视图重置）时调用。</summary>
    /// <remarks>只取消不释放：在途解码任务仍持有 Token，立即释放 CTS 会令其取消传播路径
    /// 抛 ObjectDisposedException（下游 finally 还会读 Token），进而炸断整页解码的 WhenAll；
    /// 未释放的 CTS 不含内核句柄，由 GC 终结回收即可。</remarks>
    public void CancelPendingLoad()
    {
        _loadCts?.Cancel();
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
