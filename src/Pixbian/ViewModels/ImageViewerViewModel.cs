/**
 * 图片查看器视图模型（M3）。
 * 职责：管理当前查看的图片、缩放比例与旋转角度，
 *      以及滚轮行为（缩放 / 翻页）与打开时的缩放首选项（适应窗口 / 实际大小）；
 *      EXIF 信息仅用于方向校正显示角度。
 *      幻灯片放映已独立为 SlideShowViewModel，本类仅提供移交请求（由外壳接管当前上下文），
 *      不持有放映定时器与播放序列。
 * 复用约定：EXIF 方向按需异步读取；编辑一律通过 IImageEditService 输出到新文件，绝不覆盖原图；
 *          查看器配置由外壳在设置变更时经 ApplySettings 推送，本类不反向依赖设置服务。
 * 关键约束：缩放比例必须钳制在上下限内，否则会出现图像尺寸为 0 或内存暴涨；
 *          全图解码前必须经 ApplyDecodeLimit 限制最长边，否则超大图一次解码即吃掉数百 MB 并 OOM 闪退；
 *          切换条目时旧图必须留存在 PreviousImage 直到转场动画播完，
 *          新图解码是异步的，提前清空会让每切一张就闪一次背景；
 *          转场动画在解码完成后的线程上发出请求，页面必须自行切回 UI 线程再播放；
 *          旋转状态分为"显示旋转"（界面渲染变换）与"落盘旋转"（写回文件）两种，
 *          前者只影响显示，后者才修改文件，二者不得混淆；
 *          EXIF 方向读取失败按无方向处理，仅影响显示朝向，不中断图片加载。
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;
using Pixbian.Imaging.Models;
using Pixbian.Imaging.Services;
using Pixbian.Services;
using Windows.Storage;

namespace Pixbian.ViewModels;

/// <summary>图片查看器视图模型。</summary>
public sealed partial class ImageViewerViewModel : ObservableObject
{
    /// <summary>缩放下限即适应窗口（1.0）：图像最小只能到完整可见，只允许继续放大。</summary>
    private const double MinZoom = 1.0;
    private const double MaxZoom = 8.0;

    /// <summary>单次缩放步进系数；页面滚轮缩放亦复用该值。</summary>
    public const double ZoomStep = 1.25;

    private readonly IImageMetadataReader _metadataReader;
    private readonly IImageEditService _editService;
    private readonly IThumbnailService _thumbnails;

    private IReadOnlyList<MediaItem> _playlist = [];
    private int _currentIndex;

    [ObservableProperty]
    private MediaItem? _currentItem;

    [ObservableProperty]
    private BitmapImage? _sourceImage;

    /// <summary>低清预览：来自缩略图管线（统一 512 档，大概率命中内存/磁盘缓存）。</summary>
    [ObservableProperty]
    private BitmapImage? _previewImage;

    /// <summary>上一张图：切换条目时留存，供转场动画播完前继续显示，由 CompleteTransition 清空。</summary>
    [ObservableProperty]
    private BitmapImage? _previousImage;

    /// <summary>装载序号：快速翻页时旧的全图解码完成不得覆盖新条目的显示。</summary>
    private int _loadSequence;

    /// <summary>本次条目切换是否已请求过转场；预览图与全图两次赋值只播一次动画。</summary>
    private bool _transitionRequested;

    /// <summary>查看器实际显示的图像：全分辨率就绪前用低清预览垫场（两级加载），
    /// 二者都未就绪时继续显示上一张，避免切换条目时闪现背景。</summary>
    public BitmapImage? DisplayImage => SourceImage ?? PreviewImage ?? PreviousImage;

    partial void OnSourceImageChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(DisplayImage));
        RequestTransition();
    }

    partial void OnPreviewImageChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(DisplayImage));
        RequestTransition();
    }

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private int _rotationDegrees;

    /// <summary>初始化图片查看器视图模型。</summary>
    /// <param name="metadataReader">EXIF 元数据读取器。</param>
    /// <param name="editService">图片编辑服务。</param>
    /// <param name="thumbnails">缩略图服务。</param>
    public ImageViewerViewModel(
        IImageMetadataReader metadataReader,
        IImageEditService editService,
        IThumbnailService thumbnails)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(editService);
        ArgumentNullException.ThrowIfNull(thumbnails);

        _metadataReader = metadataReader;
        _editService = editService;
        _thumbnails = thumbnails;
    }

    /// <summary>是否可切换到上一张。</summary>
    public bool CanGoPrevious => _currentIndex > 0;

    /// <summary>是否可切换到下一张。</summary>
    public bool CanGoNext => _playlist.Count > 0 && _currentIndex < _playlist.Count - 1;

    /// <summary>当前位置的可读文本。</summary>
    public string PositionText => _playlist.Count == 0
        ? "无文件"
        : $"{_currentIndex + 1} / {_playlist.Count}";

    /// <summary>缩放比例的可读文本。</summary>
    public string ZoomText => $"{(int)Math.Round(Zoom * 100)}%";

    /// <summary>当前鼠标滚轮行为；由外壳经 ApplySettings 推送。</summary>
    public ViewerWheelMode ViewerWheelMode { get; private set; } = ViewerWheelMode.Zoom;

    /// <summary>图片打开时的初始缩放方式；由外壳经 ApplySettings 推送。</summary>
    public ViewerInitialZoom ViewerInitialZoom { get; private set; } = ViewerInitialZoom.FitToWindow;

    /// <summary>查看器条目切换的过渡方式；与幻灯片共用同一偏好，由外壳经 ApplySettings 推送。</summary>
    public SlideShowTransitionMode TransitionMode { get; private set; } = SlideShowTransitionMode.Slide;

    /// <summary>新图已可显示、可以播放转场动画时触发；页面播完动画后必须回调 CompleteTransition。</summary>
    public event EventHandler? TransitionRequested;

    /// <summary>用户请求把当前上下文移交幻灯片放映时触发；由外壳接管（关查看器、开放映窗口）。</summary>
    public event EventHandler? SlideShowHandoffRequested;

    /// <summary>按最新设置应用查看器滚轮 / 缩放首选项。</summary>
    /// <param name="settings">当前设置快照。</param>
    public void ApplySettings(AppSettings settings)
    {
        ViewerWheelMode = settings.ViewerWheelMode;
        ViewerInitialZoom = settings.ViewerInitialZoom;
        TransitionMode = settings.SlideShowTransition;
    }

    /// <summary>转场动画播完的回调：清掉留存的旧图，避免双层位图长期驻留内存。</summary>
    public void CompleteTransition()
    {
        if (PreviousImage is null)
        {
            return;
        }

        PreviousImage = null;
        OnPropertyChanged(nameof(DisplayImage));
    }

    /// <summary>设置播放列表并定位到指定条目。</summary>
    /// <param name="items">播放列表。</param>
    /// <param name="startIndex">起始索引。</param>
    public async Task LoadPlaylistAsync(IReadOnlyList<MediaItem> items, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 换播放列表即重新开始：清掉上一次的留存，避免打开查看器时先闪一张上回看过的图。
        // 三个都要清——DisplayImage 的先后是「新图 → 留存旧图」，而旧图本身还挂在 SourceImage
        // 上（单例窗口复用），只清 PreviousImage 仍会显示上一张，直到新图解码完成。
        PreviousImage = null;
        SourceImage = null;
        PreviewImage = null;

        _playlist = items;
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, items.Count - 1));

        if (items.Count == 0)
        {
            CurrentItem = null;
            SourceImage = null;
            return;
        }

        await LoadCurrentAsync();
    }

    /// <summary>加载当前条目的图像与元数据。</summary>
    [RelayCommand]
    public async Task LoadCurrentAsync()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        // 先把当前显示的图留存为「上一张」：新图解码是异步的，期间继续显示旧图可避免闪现背景。
        // 首次装载时 DisplayImage 为 null，留存亦为 null，自然不会触发转场。
        PreviousImage = DisplayImage;
        _transitionRequested = false;

        CurrentItem = _playlist[_currentIndex];

        // 实际大小模式不预设适应窗口：保留当前缩放（通常即上一张的实际大小值），
        // 由页面在条目切换与全图就绪时应用实际大小，避免「先适应窗口再放大」的跳变。
        if (ViewerInitialZoom == ViewerInitialZoom.FitToWindow)
        {
            SetZoom(1.0);
        }

        RotationDegrees = 0;

        NotifyPositionChanged();
        await LoadImageAsync();

        // 新图解码失败时 DisplayImage 会回落到留存的旧图，本次没有转场可播，
        // 必须主动清掉留存：否则旧位图被长期持有，界面还会停在旧图上却提示加载失败。
        if (ReferenceEquals(DisplayImage, PreviousImage))
        {
            PreviousImage = null;
        }
    }

    /// <summary>切换到上一张。</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    public async Task GoPreviousAsync()
    {
        if (!CanGoPrevious)
        {
            return;
        }

        _currentIndex--;
        await LoadCurrentAsync();
    }

    /// <summary>切换到下一张。</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    public async Task GoNextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        _currentIndex++;
        await LoadCurrentAsync();
    }

    /// <summary>放大。</summary>
    /// <summary>是否可放大：未达缩放上限。</summary>
    public bool CanZoomIn => Zoom < MaxZoom - 0.0001;

    /// <summary>是否可缩小：未到适应窗口下限。</summary>
    public bool CanZoomOut => Zoom > MinZoom + 0.0001;

    /// <summary>放大。</summary>
    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    public void ZoomIn() => SetZoom(Zoom * ZoomStep);

    /// <summary>缩小。</summary>
    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    public void ZoomOut() => SetZoom(Zoom / ZoomStep);

    /// <summary>重置为适应窗口（100%）。</summary>
    [RelayCommand]
    public void ResetZoom() => SetZoom(1.0);

    /// <summary>顺时针旋转 90 度（仅影响显示，不修改文件）。</summary>
    [RelayCommand]
    public void RotateRight() => RotationDegrees = (RotationDegrees + 90) % 360;

    /// <summary>逆时针旋转 90 度（仅影响显示，不修改文件）。</summary>
    [RelayCommand]
    public void RotateLeft() => RotationDegrees = (RotationDegrees + 270) % 360;

    /// <summary>把当前上下文移交幻灯片放映：外壳收到事件后关闭查看器并打开放映窗口。</summary>
    [RelayCommand(CanExecute = nameof(HasMultipleItems))]
    public void HandoffSlideShow() => SlideShowHandoffRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>设置缩放比例（内部钳制上下限），供页面滚轮/双击等交互调用。</summary>
    /// <param name="value">目标缩放比例。</param>
    public void SetZoom(double value)
    {
        Zoom = Math.Clamp(value, MinZoom, MaxZoom);

        // Zoom 由 [ObservableProperty] 生成，其 OnXxxChanged 钩子在本项目的拆分投影下
        // 无法用标准签名配对（CS8799），故派生属性的联动通知在赋值点手动发出。
        OnPropertyChanged(nameof(ZoomText));
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把当前显示角度落盘保存为新文件。</summary>
    /// <param name="destinationPath">输出路径；本方法不做同路径校验，
    /// 与源文件相同时由 ImageEditService.RotateAsync 抛出 ArgumentException。</param>
    public async Task SaveRotationAsync(string destinationPath)
    {
        if (CurrentItem is null)
        {
            return;
        }

        var quarterTurns = (RotationDegrees % 360) / 90;

        if (quarterTurns == 0)
        {
            throw new InvalidOperationException("当前无需旋转，未生成新文件。");
        }

        await _editService.RotateAsync(CurrentItem.Path, destinationPath, quarterTurns);
    }

    private bool HasMultipleItems => _playlist.Count > 1;

    /// <summary>在满足条件的首个可显示时机请求一次转场；预览图与全图两次赋值只播一次动画。</summary>
    private void RequestTransition()
    {
        // 无旧图可对照（首次装载、重载当前条目）时不播动画，否则会看到一次无意义的淡入。
        if (_transitionRequested || PreviousImage is null || DisplayImage is null)
        {
            return;
        }

        if (ReferenceEquals(DisplayImage, PreviousImage))
        {
            return;
        }

        _transitionRequested = true;
        TransitionRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadImageAsync()
    {
        if (CurrentItem is null)
        {
            return;
        }

        var sequence = ++_loadSequence;
        var path = CurrentItem.Path;
        var item = CurrentItem;

        // 两级加载：先取低清预览（缩略图管线 512 档，大概率命中内存/磁盘缓存，亚秒出图），
        // 全分辨率解码完成后再替换——大图首帧等待从数秒降到一个刷新周期。
        // 预览与全图共用当前条目，切换条目时双双置空。
        SourceImage = null;
        PreviewImage = null;

        try
        {
            var preview = await _thumbnails.GetThumbnailAsync(path, 512, CancellationToken.None);

            if (sequence == _loadSequence)
            {
                PreviewImage = preview;
            }
        }
        catch (Exception)
        {
            // 预览是加速层，任何失败都只影响首帧清晰度，不阻塞全图加载。
        }

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

            var bitmap = new BitmapImage();
            ApplyDecodeLimit(bitmap, item);
            await bitmap.SetSourceAsync(stream);

            // 快速翻页时旧的全图解码可能在新条目显示后才完成，
            // 装载序号过期即丢弃，否则旧图会错配到新条目名下。
            if (sequence != _loadSequence)
            {
                return;
            }

            SourceImage = bitmap;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            if (sequence == _loadSequence)
            {
                SourceImage = null;
            }
        }

        if (sequence != _loadSequence)
        {
            return;
        }

        await ApplyExifOrientationAsync();
    }

    /// <summary>
    /// 全图解码的最长边上限（像素）：超过时按等比设置 <see cref="BitmapImage.DecodePixelWidth"/> 降采样解码。
    /// </summary>
    /// <remarks>
    /// 全分辨率位图内存约为「宽 × 高 × 4」字节，12000 × 9000 的单张即约 432 MB；
    /// 与转场期间留存的上一张叠加足以触发 OOM 闪退——这类崩溃常无托管堆栈，极难定位。
    /// 阈值取 8192：手机与单反照片普遍不超过 6000 最长边，日常放大清晰度零回退，
    /// 仅全景拼接、高精扫描等极端图被降采样。
    /// 后续若要支持「放大到 1:1 仍取原图」，应在此之上按缩放级别重载原图（分级解码），
    /// 不可直接取消本限制。
    /// </remarks>
    private const int FullDecodeMaxEdge = 8192;

    /// <summary>按上限为超大图设置解码尺寸；尺寸未知（回填未完成）时不做限制，保持原分辨率。</summary>
    /// <param name="bitmap">待解码的位图；DecodePixel* 必须在 SetSourceAsync 之前设置。</param>
    /// <param name="item">当前条目，提供索引到的像素尺寸。</param>
    private static void ApplyDecodeLimit(BitmapImage bitmap, MediaItem? item)
    {
        var width = item?.Width ?? 0;
        var height = item?.Height ?? 0;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var longestEdge = Math.Max(width, height);

        if (longestEdge <= FullDecodeMaxEdge)
        {
            return;
        }

        var scale = (double)FullDecodeMaxEdge / longestEdge;

        bitmap.DecodePixelWidth = (int)Math.Round(width * scale);
        bitmap.DecodePixelHeight = (int)Math.Round(height * scale);
    }

    /// <summary>读取 EXIF 方向并校正显示角度，避免照片躺着或倒着显示；失败仅影响朝向。</summary>
    private async Task ApplyExifOrientationAsync()
    {
        if (CurrentItem is null)
        {
            return;
        }

        try
        {
            var metadata = await _metadataReader.ReadAsync(CurrentItem.Path);

            // 元数据返回后才知悉 EXIF 方向，此时再校正显示角度。
            if (metadata?.Orientation is { } orientation)
            {
                RotationDegrees = ExifOrientationToDegrees(orientation);
            }
        }
        catch (Exception)
        {
            // 方向校正是加速层之外的可选环节，任何失败都不应中断图片显示。
        }
    }

    /// <summary>把 EXIF 方向标记换算为顺时针旋转角度，使照片以正确朝向显示。</summary>
    private static int ExifOrientationToDegrees(int orientation) => orientation switch
    {
        3 => 180,
        6 => 90,
        8 => 270,
        _ => 0
    };

    private void NotifyPositionChanged()
    {
        OnPropertyChanged(nameof(PositionText));
        GoPreviousCommand.NotifyCanExecuteChanged();
        GoNextCommand.NotifyCanExecuteChanged();
        HandoffSlideShowCommand.NotifyCanExecuteChanged();
    }
}
