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
using Microsoft.UI.Xaml.Media;
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

    /// <summary>选择模式复选框是否可见可点：页面在选择模式进出与集合替换时批量同步。</summary>
    /// <remarks>ItemsRepeater 无容器机制，复选框显隐只能由条目属性驱动（x:Bind 根为条目 VM，
    /// 页面级属性在模板中不可达）；批量写发生在模式切换与集合替换，realized 元素之外的写入
    /// 仅落字段，无布局成本。</remarks>
    [ObservableProperty]
    private bool _isSelectionCheckVisible;

    /// <summary>是否已收藏；只承载界面展示状态，数据库持久化由调用方完成。</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>已收藏态图标画刷：60% 不透明度红。</summary>
    private static readonly Brush FavoriteActiveBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0x00, 0x00));

    /// <summary>未收藏态图标画刷：60% 不透明度白。</summary>
    private static readonly Brush FavoriteInactiveBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private double _displayWidth;
    private double _displayHeight;
    /// <summary>最近一次对外通知的宽高比；NaN 表示尚未通知过，首次通知必发。</summary>
    private double _notifiedAspectRatio = double.NaN;

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
        IsFavorite = item.IsFavorite;
    }

    /// <summary>底层媒体条目。</summary>
    /// <remarks>收藏切换经 <see cref="SetFavorite"/> 原位刷新，实例不替换。</remarks>
    public MediaItem Item { get; private set; }

    /// <summary>该条目是否生成过显示容器。</summary>
    /// <remarks>
    /// 用于区分「从未进入视口」与「进入过视口后被回收」：虚拟化列表的
    /// ContainerFromItem 对这两种情形都返回 null，无法区分。若不加区分地按它取消在途解码，
    /// 整页提交（为尚未生成容器的条目预取缩略图）会被整批取消，而条目自身又因
    /// 在途标记已置位而拒绝重新发起，缩略图将永不出现——表现为界面「冻结」。
    /// 由页面在 ContainerContentChanging 时置位。
    /// </remarks>
    public bool ContainerEverRealized { get; set; }

    /// <summary>主键。</summary>
    public long Id => Item.Id;

    /// <summary>文件名。</summary>
    public string FileName => Item.FileName;

    /// <summary>是否为视频。</summary>
    public bool IsVideo => Item.Kind == MediaKind.Video;

    /// <summary>收藏图标字形：未收藏为空心爱心，已收藏为实心爱心。</summary>
    public string FavoriteGlyph => IsFavorite ? "\uEB52" : "\uEB51";

    /// <summary>收藏图标画刷：未收藏为 80% 白，已收藏为 80% 红。</summary>
    public Brush FavoriteBrush => IsFavorite ? FavoriteActiveBrush : FavoriteInactiveBrush;

    /// <summary>原位更新收藏状态；数据库持久化由调用方先行完成。</summary>
    /// <remarks>
    /// 就地刷新而非「新建实例替换集合项」：替换会丢弃已解码缩略图与显示容器，
    /// 条目闪回骨架屏重新排队解码，且正在播放的收藏动画会因元素重建而中断。
    /// </remarks>
    public void SetFavorite(bool isFavorite)
    {
        Item = Item with { IsFavorite = isFavorite };
        IsFavorite = isFavorite;
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteBrush));
    }

    /// <summary>宽高比；按预取尺寸、索引尺寸、位图尺寸的顺序取值，均缺失时按方图处理。</summary>
    /// <remarks>
    /// 预取尺寸先于位图到位，使布局在缩略图解码完成前就按真实比例排列，
    /// 否则每个条目都要先从方图跳到真实比例、整行跟着重排。钳制到合理区间防止布局极端。
    /// <para>
    /// 位图尺寸必须排在最后而不是最前：Thumbnail 是按显示区降采样解码的（档位 128~2560），
    /// 其宽高比经档位量化后与原图存在微小差异。若让它在缩略图到位后覆盖已取到的准确值，
    /// 那么每次因显示尺寸变化而重新解码，都会让本属性抖动一次，面板随之重排并回写新的显示尺寸，
    /// 尺寸越界又触发再次解码——形成「解码 → 抖动 → 重排 → 解码」的布局循环，
    /// 表现为吃满一个 CPU 核心、界面完全无响应，且托管堆栈为空、崩溃日志不留痕迹。
    /// 位图仅在预取与索引均无尺寸时兜底，此刻它虽是近似值，但仍远好于退化成方图。
    /// </para>
    /// </remarks>
    public double AspectRatio
    {
        get
        {
            // 取值顺序必须保持「预取 > 索引 > 位图兜底」：位图按档位量化解码，宽高比与原图
            // 存在微小偏差，一旦让它覆盖已有准确值就会形成「解码 → 比例抖动 → 重排 → 回写 →
            // 再解码」的布局循环（84c9e74 基线实证触发 LayoutCycleException）。位图仅在
            // 预取与索引均无尺寸时兜底，此刻它虽是近似值，但仍远好于退化成方图。
            if (_probeWidth is > 0 && _probeHeight is > 0)
            {
                return FromDimensions(_probeWidth.Value, _probeHeight.Value);
            }

            if (Item.Width is > 0 && Item.Height is > 0)
            {
                return FromDimensions(Item.Width.Value, Item.Height.Value);
            }

            if (GetPixelDimensions() is { Width: > 0, Height: > 0 } bitmap)
            {
                return FromDimensions(bitmap.Width, bitmap.Height);
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
        NotifyAspectRatioChanged();
        OnPropertyChanged(nameof(DimensionText));
        OnPropertyChanged(nameof(VideoBadgeText));
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
    /// <remarks>
    /// 宽高比通知经去重：索引/预取就位后宽高比已稳定，位图更换不会改变其值，
    /// 此时发通知只会让布局面板空转重测（200 条逐张解码 = 200 次全量重排）。
    /// </remarks>
    partial void OnThumbnailChanged(BitmapImage? value)
    {
        // 位图替换原则上不驱动布局通知：几何仅由预取/索引尺寸决定（先行且稳定），
        // 位图档位量化的比例偏差若参与通知，每张图到位都会引发全量重排——数百条
        // × O(n) 重排会钉死 UI 线程（实测加载总耗时随集合规模恶化至 11 秒）。
        // 仅当预取与索引尺寸均缺失、位图是唯一比例来源时才允许通知。
        if (_probeWidth is null && Item.Width is null)
        {
            NotifyAspectRatioChanged();
        }

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

    /// <summary>宽高比变更的去重通知：值不变时静默，避免驱动布局面板空转重测。</summary>
    /// <remarks>
    /// 布局几何只取决于宽高比。该值在预取或索引尺寸就位后即稳定，此后位图更换、
    /// 升级解码都不改变它；若仍逐次通知，面板每次都全量重排（不做虚拟化），
    /// 与「回写显示尺寸 → 触发再解码」叠加会形成正反馈，是布局循环的温床。
    /// 首次通知必发（哨兵为 NaN），保证初始绑定之后的布局能拿到真实比值。
    /// </remarks>
    private void NotifyAspectRatioChanged()
    {
        var ratio = AspectRatio;

        if (!double.IsNaN(_notifiedAspectRatio) && Math.Abs(ratio - _notifiedAspectRatio) < 0.0001)
        {
            return;
        }

        _notifiedAspectRatio = ratio;
        OnPropertyChanged(nameof(AspectRatio));
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

    /// <summary>是否需要打开文件探测尺寸；索引中已回填宽高时无需再读文件头。</summary>
    /// <remarks>
    /// 后台元数据回填完成后，条目的宽高直接来自索引，AspectRatio 与 DimensionText 都能取到值，
    /// 此时再探测文件头纯属浪费——每次加载都要为每个条目开一次文件流，是文件夹加载的主要耗时项。
    /// </remarks>
    public bool NeedsDimensionProbe => Item.Width is null && Item.Height is null;

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
    /// <remarks>索引与预取都可能给出宽高，故按「预取尺寸 > 索引字段」取值。
    /// 此处与 AspectRatio 一致地不读 Thumbnail：它是按显示区降采样解码的位图（档位 128~2560），
    /// 像素是缩略图大小而非原图分辨率，用作分辨率会直接给出错误数值。
    /// 同一份位图数据对宽高比也不安全——档位量化会让比值相对原图产生微小偏差，
    /// 该偏差足以驱动布局循环，详见 AspectRatio 的说明。</remarks>
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

    /// <summary>列表角标文本：画质档位在前、时长在后（例："1080P · 12:34"）；取不到档位时只有时长。</summary>
    /// <remarks>合成为单个文本而非两个并列 TextBlock：模板内 x:Bind 不能配 StaticResource Converter，
    /// 无法用 Visibility 单独隐藏档位，分列会在无档位时留下无法消除的间隔。</remarks>
    public string VideoBadgeText =>
        QualityText.Length == 0 ? DurationText : $"{QualityText} · {DurationText}";

    /// <summary>视频画质档位（480P / 720P / 1080P / 2K / 4K）；尺寸未知或低于 480P 时为空串。</summary>
    /// <remarks>按短边判定：竖屏视频（如 1080×1920）的短边才是「多少 P」的依据。
    /// 尺寸来源与 DimensionText 一致（预取优先于索引），不读缩略图位图——它是按显示区降采样的。</remarks>
    public string QualityText =>
        ResolvedDimensions is { } size ? FormatQuality(size.Width, size.Height) : string.Empty;

    /// <summary>已解析的像素尺寸：预取尺寸优先，其次索引字段，均未就位时为 null。</summary>
    private (int Width, int Height)? ResolvedDimensions =>
        _probeWidth is > 0 && _probeHeight is > 0
            ? (_probeWidth.Value, _probeHeight.Value)
            : Item.Width is > 0 && Item.Height is > 0
                ? (Item.Width.Value, Item.Height.Value)
                : null;

    /// <summary>按短边把像素尺寸归到画质档位。</summary>
    /// <param name="width">像素宽度。</param>
    /// <param name="height">像素高度。</param>
    private static string FormatQuality(int width, int height) =>
        Math.Min(width, height) switch
        {
            >= 2160 => "4K",
            >= 1440 => "2K",
            >= 1080 => "1080P",
            >= 720 => "720P",
            >= 480 => "480P",
            _ => string.Empty
        };

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
