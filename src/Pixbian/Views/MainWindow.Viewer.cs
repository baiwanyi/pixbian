/**
 * 主窗口代码后置——查看器与播放态（partial）。
 * 职责：图片查看器窗口与放映窗口的创建/复用/移交，视频播放器装进播放态宿主与卸载，
 *      全屏切换，以及「常态外壳 ↔ 播放态独立根」的窗口级 chrome 切换。
 * 复用约定：查看器与放映窗口按需经 App.Services 解析（窗口持有页面，注入会成环）；
 *          播放列表统一取图库当前列表；视频页 OpenAsync 承载队列起播。
 * 关键约束：装载必须先让播放态根可见再赋 Content（往 Collapsed 容器塞内容不触发 Loaded），
 *          卸载必须置空 Content（仅折叠容器不触发 Unloaded，MediaPlayer 不会释放）；
 *          全屏切换用 AppWindow.SetPresenter（Presenter.Kind 只读）。
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Services;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>主窗口的查看器与播放态。</summary>
public sealed partial class MainWindow
{
    /// <summary>当前打开的图片查看器窗口；窗口关闭（Closed）后置 null，下次打开创建新实例。</summary>
    private ImageViewerWindow? _imageViewerWindow;

    /// <summary>当前打开的幻灯片放映窗口；窗口关闭（Closed）后置 null，下次打开创建新实例。</summary>
    private SlideShowWindow? _slideShowWindow;

    private bool _isViewerVisible;

    /// <summary>切换导航时必须关闭查看器，否则会停留在查看状态却显示导航页。</summary>
    private void CloseViewerIfVisible()
    {
        if (!IsViewerVisible)
        {
            return;
        }

        // 必须在播放态容器仍可见时卸载页面：Collapsed 容器不会触发 Unloaded，
        // MediaPlayer 就得不到释放（解码器不回收，反复进出播放会内存持续增长）。
        DetachVideoPage();

        IsViewerVisible = false;
        ExitFullScreenIfNeeded();
        OnChromeVisibilityChanged();
    }

    /// <summary>退出系统全屏，还原查看器打开前的窗口形态；未全屏时跳过（视频播放器路径）。</summary>
    private void ExitFullScreenIfNeeded()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            ToggleFullScreen();
        }
    }

    /// <summary>在图库中双击条目时打开查看器：图片走图片查看器，视频走播放器。</summary>
    /// <param name="item">被双击的条目。</param>
    public async Task OpenViewerAsync(MediaItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var items = _gallery.Items.Select(i => i.Item).ToList();

        if (item.Item.Kind == MediaKind.Video)
        {
            // 播放队列取图库当前列表中的视频子集：上/下一条在当前筛选结果内连续切换。
            var videos = items.Where(i => i.Kind == MediaKind.Video).ToList();
            var videoIndex = Math.Max(0, videos.FindIndex(i => i.Id == item.Id));
            await OpenVideoPlayerAsync(videos, videoIndex);
            return;
        }

        if (item.Item.Kind != MediaKind.Image)
        {
            return;
        }

        var index = items.FindIndex(i => i.Id == item.Id);

        // 查看器以独立全屏窗口打开，主窗口保持原样；已有未关闭的查看器窗口时直接复用
        // （重载播放列表并带到前台），避免叠加多个全屏窗口。
        var viewerWindow = _imageViewerWindow;

        if (viewerWindow is null)
        {
            viewerWindow = App.Services.GetRequiredService<ImageViewerWindow>();
            viewerWindow.Closed += (_, _) => _imageViewerWindow = null;
            _imageViewerWindow = viewerWindow;
        }

        viewerWindow.Activate();
        viewerWindow.ViewerPage.BeginOpen();

        await _viewer.LoadPlaylistAsync(items, Math.Max(0, index));
    }

    /// <summary>打开幻灯片放映窗口：以图库当前列表为候选（是否含视频按设置过滤），从指定条目起播。</summary>
    /// <param name="candidates">放映候选列表。</param>
    /// <param name="start">起始条目。</param>
    /// <remarks>
    /// 已有未关闭的放映窗口时直接复用（重载列表并带到前台），避免叠加多个全屏窗口；
    /// 放映配置经放映视图模型的 ApplySettings 推送，与设置页变更保持同一链路。
    /// </remarks>
    public async Task OpenSlideShowAsync(IReadOnlyList<MediaItemViewModel> candidates, MediaItemViewModel start)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(start);

        var items = candidates.Select(i => i.Item).ToList();
        var settings = _settings.Settings;

        var window = _slideShowWindow;

        if (window is null)
        {
            window = App.Services.GetRequiredService<SlideShowWindow>();
            window.Closed += (_, _) => _slideShowWindow = null;
            _slideShowWindow = window;
        }

        window.Activate();

        var viewModel = window.Page.ViewModel;

        // 设置必须先于 BeginOpen：页面的背景层初值取自视图模型当前的虚化开关，
        // 顺序颠倒会让首次打开的舞台沿用上一轮（或默认）的虚化状态。
        viewModel.ApplySettings(settings);
        window.Page.BeginOpen();

        await viewModel.LoadPlaylistAsync(items, start.Item);
        viewModel.StartCommand.Execute(null);
    }

    /// <summary>查看器请求移交放映：以查看器当前列表与条目开放映窗口，随后关闭查看器。</summary>
    private async void OnViewerSlideShowHandoffRequested(object? sender, EventArgs e)
    {
        var viewerWindow = _imageViewerWindow;

        if (viewerWindow is null)
        {
            return;
        }

        var currentItem = viewerWindow.ViewerPage.ViewModel.CurrentItem;
        var candidates = _gallery.Items.ToList();

        // 先关查看器再开放映：避免两个全屏置顶窗口短暂叠加争焦点。
        viewerWindow.Close();

        var start = candidates.FirstOrDefault(i => currentItem is not null && i.Id == currentItem.Id)
            ?? candidates.FirstOrDefault(i => !i.IsVideo);

        if (start is null)
        {
            return;
        }

        await OpenSlideShowAsync(candidates, start);
    }

    /// <summary>按领域模型打开视频播放器；供短片页「查看原视频」等非图库入口使用。</summary>
    /// <param name="item">媒体条目；非视频类型直接返回。</param>
    /// <remarks>与图库双击共用同一条装载链路，避免两套代码在「播放态切换」上各自漂移。</remarks>
    public async Task OpenViewerAsync(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Kind == MediaKind.Video)
        {
            await OpenVideoPlayerAsync([item], 0);
        }
    }

    /// <summary>按文件路径打开媒体：文件激活的统一入口，图片进查看器窗口，视频进播放器。</summary>
    /// <param name="path">媒体文件完整路径；不受支持或文件不可读时静默返回。</param>
    /// <remarks>
    /// 装载的是「未入库条目」（主键 0）：双击的文件未必属于任何媒体库，
    /// 故不写索引库，只用文件系统的信息构造条目供查看器与播放器使用。
    /// </remarks>
    public async Task OpenFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !MediaFileClassifier.IsSupported(path))
        {
            return;
        }

        var item = UnindexedMediaItemFactory.Create(path);

        if (item is null)
        {
            return;
        }

        if (item.Kind == MediaKind.Video)
        {
            await OpenVideoPlayerAsync([item], 0);
            return;
        }

        await OpenImagesAsync([item], 0);
    }

    /// <summary>以指定条目列表打开图片查看器；供文件激活等非图库入口使用。</summary>
    /// <param name="items">播放列表（调用方已过滤出图片条目）。</param>
    /// <param name="startIndex">起始索引，越界时钳制到列表范围内。</param>
    /// <remarks>与图库双击共用查看器窗口的复用规则，避免同时存在多个全屏窗口。</remarks>
    public async Task OpenImagesAsync(IReadOnlyList<MediaItem> items, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        var viewerWindow = _imageViewerWindow;

        if (viewerWindow is null)
        {
            viewerWindow = App.Services.GetRequiredService<ImageViewerWindow>();
            viewerWindow.Closed += (_, _) => _imageViewerWindow = null;
            _imageViewerWindow = viewerWindow;
        }

        viewerWindow.Activate();
        viewerWindow.ViewerPage.BeginOpen();

        await _viewer.LoadPlaylistAsync(items, Math.Clamp(startIndex, 0, items.Count - 1));
    }

    /// <summary>把播放器页装进播放态宿主并按队列起播指定视频。</summary>
    /// <param name="items">视频队列（调用方过滤掉非视频条目）。</param>
    /// <param name="startIndex">起始索引。</param>
    private async Task OpenVideoPlayerAsync(IReadOnlyList<MediaItem> items, int startIndex)
    {
        // 播放器页延迟解析：其 MediaPlayerElement 在应用启动阶段构造会触发 WinRT 异常。
        var videoPage = App.Services.GetRequiredService<VideoPlayerPage>();

        // 顺序不可调换：先显示播放态根，再把页面装进 VideoHost——
        // 往 Collapsed 的容器里塞内容不会触发 Loaded，页面初始化（含顶栏布局）会被整段跳过。
        IsViewerVisible = true;
        OnChromeVisibilityChanged();
        AttachVideoPage(videoPage);

        await videoPage.OpenAsync(items, startIndex);
    }

    /// <summary>当前是否处于全屏演示态（Esc 分级退出需要先判断）。</summary>
    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>切换全屏状态。</summary>
    public void ToggleFullScreen()
    {
        // AppWindowPresenter.Kind 是只读的，切换演示器必须使用 SetPresenter。
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        // 播放器页需感知全屏切换：全屏时隐藏底栏、全屏按钮切换为「返回窗口」。
        if (VideoHost.Content is VideoPlayerPage videoPage)
        {
            videoPage.OnWindowFullScreenChanged(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        }
    }

    /// <summary>播放态切换的收口：通知导航栏收起，并切换窗口级分支。</summary>
    private void OnChromeVisibilityChanged()
    {
        OnPropertyChanged(nameof(NavigationPaneMode));
        ApplyViewerChrome();
    }

    /// <summary>在「常态外壳」与「播放态独立根」之间切换，两者结构互不影响。</summary>
    /// <remarks>
    /// 播放态隐藏标题栏行与导航栏，只显示 PlayerRoot（覆盖标题栏行与内容行），
    /// 画面顶到窗口最上沿，顶栏由播放器页自绘并与系统窗口按钮同排；常态反向切回。
    /// 内容卡片的外观属性全程不被改写，故无需在运行期覆盖与还原。
    /// </remarks>
    private void ApplyViewerChrome()
    {
        var isVideo = IsViewerVisible;

        TitleBar.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        NavigationViewControl.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        PlayerRoot.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;

        // 播放态背景被 PlayerRoot 完全遮挡：一并隐藏，避免背景层继续参与每帧合成。
        WallpaperLayer.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

        // 播放态舞台恒为暗色，系统按钮配色须随分支切换重算（浅色主题下不能沿用黑字）。
        UpdateCaptionButtonColors();

        SchedulePassthroughRefresh();
    }

    /// <summary>把播放器页装载到播放态宿主。</summary>
    /// <param name="videoPage">播放器页（依赖注入单例）。</param>
    /// <remarks>
    /// 播放器页只能在 VideoHost 这一处；重复装载同一实例直接跳过，避免 Content 反复变更
    /// 触发无谓的 Unloaded/Loaded。装载后立即排一帧刷新 Passthrough：调用链上
    /// ApplyViewerChrome 的刷新排在装载之前，那一拍读到的 Content 还是空。
    /// 左上角常驻返回按钮落在系统标题栏区域内，必须经 Passthrough 放行才能收到点击。
    /// </remarks>
    private void AttachVideoPage(VideoPlayerPage videoPage)
    {
        if (!ReferenceEquals(VideoHost.Content, videoPage))
        {
            VideoHost.Content = videoPage;
        }

        SchedulePassthroughRefresh();
    }

    /// <summary>卸载播放器页：置空 Content 触发其 Unloaded，进而释放 MediaPlayer。</summary>
    /// <remarks>
    /// 仅把 PlayerRoot 切为 Collapsed 不会触发 Unloaded，解码器就得不到回收，
    /// 反复进出播放会导致内存持续增长。
    /// </remarks>
    private void DetachVideoPage()
    {
        VideoHost.Content = null;
    }

    /// <summary>排到下一帧刷新标题栏放行区域：可见性刚变时布局尚未重算，立即取矩形会拿到旧值。</summary>
    private void SchedulePassthroughRefresh() =>
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            UpdateTitleBarPassthrough);

    /// <summary>关闭视频播放器，返回图库（图片查看器为独立窗口，经其自身 Close 关闭）。</summary>
    public void CloseViewer()
    {
        // 卸载播放器页即触发其 Unloaded，进而释放 MediaPlayer；须在容器仍可见时执行。
        // 先退全屏再卸载，避免全屏演示器切换与视觉树变更在同一帧叠加。
        ExitFullScreenIfNeeded();
        DetachVideoPage();

        IsViewerVisible = false;
        OnChromeVisibilityChanged();

        ApplyCurrentPage(_currentTarget);
    }
}
