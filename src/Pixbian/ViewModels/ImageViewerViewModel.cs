/**
 * 图片查看器视图模型（M3）。
 * 职责：管理当前查看的图片、缩放比例、旋转角度、幻灯片播放与元数据加载。
 * 复用约定：元数据按需异步加载；编辑一律通过 IImageEditService 输出到新文件，绝不覆盖原图。
 * 关键约束：缩放比例必须钳制在上下限内，否则会出现图像尺寸为 0 或内存暴涨；
 *          幻灯片定时器必须在切换图片或离开页面时停止，否则会残留后台计时器持续触发；
 *          旋转状态分为"显示旋转"（界面渲染变换）与"落盘旋转"（写回文件）两种，
 *          前者只影响显示，后者才修改文件，二者不得混淆。
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

namespace Pixbian.ViewModels;

/// <summary>图片查看器视图模型。</summary>
public sealed partial class ImageViewerViewModel : ObservableObject, IDisposable
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.25;

    private readonly IImageMetadataReader _metadataReader;
    private readonly IImageEditService _editService;
    private readonly DispatcherQueueTimer _slideShowTimer;

    private IReadOnlyList<MediaItem> _playlist = [];
    private int _currentIndex;

    [ObservableProperty]
    private MediaItem? _currentItem;

    [ObservableProperty]
    private BitmapImage? _sourceImage;

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
        DispatcherQueue? dispatcherQueue = null)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(editService);

        _metadataReader = metadataReader;
        _editService = editService;

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

    /// <summary>幻灯片间隔。</summary>
    public TimeSpan SlideShowInterval
    {
        get => _slideShowTimer.Interval;
        set => _slideShowTimer.Interval = value;
    }

    /// <summary>设置播放列表并定位到指定条目。</summary>
    /// <param name="items">播放列表。</param>
    /// <param name="startIndex">起始索引。</param>
    public async Task LoadPlaylistAsync(IReadOnlyList<MediaItem> items, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        StopSlideShow();

        _playlist = items;
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, items.Count - 1));

        if (items.Count == 0)
        {
            CurrentItem = null;
            SourceImage = null;
            Metadata = null;
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

        CurrentItem = _playlist[_currentIndex];
        Zoom = 1.0;
        RotationDegrees = 0;
        Metadata = null;

        NotifyPositionChanged();
        await LoadImageAsync();
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

    /// <inheritdoc />
    public void Dispose() => _slideShowTimer.Stop();

    private bool HasMultipleItems => _playlist.Count > 1;

    private void SetZoom(double value)
    {
        Zoom = Math.Clamp(value, MinZoom, MaxZoom);
        OnPropertyChanged(nameof(ZoomText));
    }

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

        SourceImage = null;

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(CurrentItem.Path);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            SourceImage = bitmap;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            SourceImage = null;
        }

        await LoadMetadataAsync();
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
