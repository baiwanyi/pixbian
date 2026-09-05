/**
 * 图片查看器视图模型（M3）。
 * 职责：管理当前查看的图片、缩放比例、旋转角度、幻灯片播放与元数据加载，
 *      并按设置应用幻灯片间隔与切换方式（滑动 / 淡出）。
 * 复用约定：元数据按需异步加载；编辑一律通过 IImageEditService 输出到新文件，绝不覆盖原图；
 *          幻灯片配置由外壳在设置变更时经 ApplySettings 推送，本类不反向依赖设置服务。
 * 关键约束：缩放比例必须钳制在上下限内，否则会出现图像尺寸为 0 或内存暴涨；
 *          幻灯片定时器必须在切换图片或离开页面时停止，否则会残留后台计时器持续触发；
 *          切换条目时旧图必须留存在 PreviousImage 直到转场动画播完，
 *          新图解码是异步的，提前清空会让每切一张就闪一次背景；
 *          转场动画在解码完成后的线程上发出请求，页面必须自行切回 UI 线程再播放；
 *          旋转状态分为"显示旋转"（界面渲染变换）与"落盘旋转"（写回文件）两种，
 *          前者只影响显示，后者才修改文件，二者不得混淆；
 *          显示尺寸优先取文件「详细信息」Shell 属性（System.Image.Dimensions，与资源管理器一致），
 *          解析失败再回落到读文件头尺寸，二者均不随缩放/旋转变化。
 */

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;
using Pixbian.Imaging.Models;
using Pixbian.Imaging.Services;
using Pixbian.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Pixbian.ViewModels;

/// <summary>图片查看器视图模型。</summary>
public sealed partial class ImageViewerViewModel : ObservableObject, IDisposable
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.25;

    private readonly IImageMetadataReader _metadataReader;
    private readonly IImageEditService _editService;
    private readonly IThumbnailService _thumbnails;
    private readonly DispatcherQueueTimer _slideShowTimer;

    private IReadOnlyList<MediaItem> _playlist = [];
    private int _currentIndex;
    private (int Width, int Height)? _displayDimensions;

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
    private ImageMetadata? _metadata;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private int _rotationDegrees;

    [ObservableProperty]
    private bool _isSlideShowPlaying;

    [ObservableProperty]
    private bool _isMetadataLoading;

    public ImageViewerViewModel(
        IImageMetadataReader metadataReader,
        IImageEditService editService,
        IThumbnailService thumbnails,
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(editService);
        ArgumentNullException.ThrowIfNull(thumbnails);

        _metadataReader = metadataReader;
        _editService = editService;
        _thumbnails = thumbnails;

        var queue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _slideShowTimer = queue.CreateTimer();
        _slideShowTimer.Interval = TimeSpan.FromSeconds(5);
        _slideShowTimer.IsRepeating = true;
        _slideShowTimer.Tick += OnSlideShowTick;
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

    /// <summary>源图原始像素尺寸（读文件头、已计入 EXIF 方向，与资源管理器一致）；未取到时返回空串。</summary>
    public string DisplayResolutionText => _displayDimensions is { Width: > 0, Height: > 0 }
        ? $"{_displayDimensions.Value.Width} × {_displayDimensions.Value.Height}"
        : string.Empty;

    /// <summary>幻灯片间隔。</summary>
    public TimeSpan SlideShowInterval
    {
        get => _slideShowTimer.Interval;
        set => _slideShowTimer.Interval = value;
    }

    /// <summary>当前幻灯片切换方式；由外壳经 ApplySettings 推送。</summary>
    public SlideShowTransitionMode SlideShowTransition { get; private set; } = SlideShowTransitionMode.Slide;

    /// <summary>新图已可显示、可以播放转场动画时触发；页面播完动画后必须回调 CompleteTransition。</summary>
    public event EventHandler? TransitionRequested;

    /// <summary>按最新设置应用幻灯片间隔与切换方式。</summary>
    /// <param name="settings">当前设置快照。</param>
    public void ApplySettings(AppSettings settings)
    {
        var wasPlaying = IsSlideShowPlaying;

        // 先停表再改间隔、改完按原状态续跑：既避开「运行期改表」的行为差异，
        // 也保证改设置不会把正在放映的幻灯片打断。
        _slideShowTimer.Stop();
        _slideShowTimer.Interval = TimeSpan.FromSeconds(settings.SlideShowIntervalSeconds);

        if (wasPlaying)
        {
            _slideShowTimer.Start();
        }

        SlideShowTransition = settings.SlideShowTransition;
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

        StopSlideShow();

        // 换播放列表即重新开始：清掉上一次的留存，避免打开查看器时先闪一张上回看过的图。
        PreviousImage = null;

        _playlist = items;
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, items.Count - 1));

        if (items.Count == 0)
        {
            CurrentItem = null;
            SourceImage = null;
            Metadata = null;
            _displayDimensions = null;
            OnPropertyChanged(nameof(DisplayResolutionText));
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
        Zoom = 1.0;
        RotationDegrees = 0;
        Metadata = null;

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
    [RelayCommand]
    public void ZoomIn() => SetZoom(Zoom * ZoomStep);

    /// <summary>缩小。</summary>
    [RelayCommand]
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

    /// <summary>开始幻灯片播放。</summary>
    [RelayCommand(CanExecute = nameof(HasMultipleItems))]
    public void StartSlideShow()
    {
        if (!HasMultipleItems)
        {
            return;
        }

        IsSlideShowPlaying = true;
        _slideShowTimer.Start();
    }

    /// <summary>停止幻灯片播放。</summary>
    [RelayCommand]
    public void StopSlideShow()
    {
        IsSlideShowPlaying = false;
        _slideShowTimer.Stop();
    }

    /// <summary>把当前显示角度落盘保存为新文件。</summary>
    /// <param name="destinationPath">输出路径，不得与源文件相同。</param>
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

    /// <summary>缩放变化时刷新缩放文本。</summary>
    private void OnZoomChanged() => OnPropertyChanged(nameof(ZoomText));

    /// <inheritdoc />
    public void Dispose() => _slideShowTimer.Stop();

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

    private void SetZoom(double value) => Zoom = Math.Clamp(value, MinZoom, MaxZoom);

    private async void OnSlideShowTick(DispatcherQueueTimer sender, object args)
    {
        if (!CanGoNext)
        {
            StopSlideShow();
            return;
        }

        await GoNextAsync();
    }

    private async Task LoadImageAsync()
    {
        if (CurrentItem is null)
        {
            return;
        }

        var sequence = ++_loadSequence;
        var path = CurrentItem.Path;

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
            var workingSetBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            stopwatch.Stop();

            var workingSetAfter = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;

            // 快速翻页时旧的全图解码可能在新条目显示后才完成，
            // 装载序号过期即丢弃，否则旧图会错配到新条目名下。
            if (sequence != _loadSequence)
            {
                return;
            }

            SourceImage = bitmap;

            Diagnostics.Log(
                $"VIEWER|{bitmap.PixelWidth}x{bitmap.PixelHeight}|{stopwatch.ElapsedMilliseconds}"
                + $"|{workingSetBefore / 1048576}|{workingSetAfter / 1048576}");
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

        _displayDimensions = await ReadSystemDimensionsAsync(path)
            ?? await _thumbnails.GetDimensionsAsync(path);
        OnPropertyChanged(nameof(DisplayResolutionText));

        await LoadMetadataAsync();
    }

    /// <summary>直接读取文件的「详细信息」分辨率（System.Image.Dimensions，与资源管理器完全一致，已计入 EXIF 方向）。</summary>
    /// <param name="path">图片文件路径。</param>
    /// <returns>像素宽高，读取失败或系统无法解析时返回 null。</returns>
    private static readonly string[] DimensionPropertyKeys = ["System.Image.Dimensions"];
    private static readonly char[] DimensionSeparators = ['x', '×', 'X', '*'];

    private static async Task<(int Width, int Height)?> ReadSystemDimensionsAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.RetrievePropertiesAsync(DimensionPropertyKeys);

            if (props.TryGetValue("System.Image.Dimensions", out var value) && value is string text)
            {
                var parts = text.Split(
                    DimensionSeparators,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (parts.Length == 2
                    && int.TryParse(parts[0], out var width)
                    && int.TryParse(parts[1], out var height)
                    && width > 0 && height > 0)
                {
                    return (width, height);
                }
            }

            return null;
        }
        catch (Exception)
        {
            // 解析失败回退到文件头尺寸
            return null;
        }
    }

    private async Task LoadMetadataAsync()
    {
        if (CurrentItem is null)
        {
            return;
        }

        IsMetadataLoading = true;

        try
        {
            var metadata = await _metadataReader.ReadAsync(CurrentItem.Path);
            Metadata = metadata;

            // 元数据返回后才知悉 EXIF 方向，此时再校正显示角度，避免照片躺着或倒着显示。
            if (metadata?.Orientation is { } orientation)
            {
                RotationDegrees = ExifOrientationToDegrees(orientation);
            }
        }
        finally
        {
            IsMetadataLoading = false;
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
        StartSlideShowCommand.NotifyCanExecuteChanged();
    }
}
