/**
 * Short 页代码后置（短视频视图）。
 * 职责：创建 MediaPlayer 并交给 ViewModel，把单击与键盘输入转换为播放控制，
 *      并在离开页面时释放播放器与背景音乐资源。
 * 复用约定：播放器由本页面惰性创建后注入 ViewModel，ViewModel 不负责创建 UI 相关对象；
 *          键盘事件订阅在页面自身而非根网格——键盘事件自焦点元素向上冒泡，
 *          焦点在页面上时不会下传到子级网格；指针事件无此限制，故单击挂在根网格。
 * 关键约束：必须在 Unloaded 时释放播放器，否则解码器不会回收，反复进出会导致内存持续增长；
 *          三个覆盖层按钮必须就地拦截 Tapped，否则点击会冒泡到舞台而顺带触发播放/暂停；
 *          主窗口经 App.Services 在需要时解析——直接注入会与「窗口持有本页」形成循环依赖。
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.Services;
using Pixbian.ViewModels;
using Windows.Media.Playback;
using Windows.System;

namespace Pixbian.Views;

/// <summary>短视频视图页。</summary>
public sealed partial class ShortPage : Page, IDisposable
{
    private MediaPlayer? _player;

    /// <summary>初始化 Short 页。</summary>
    /// <param name="viewModel">短片页视图模型，由依赖注入提供。</param>
    public ShortPage(ShortViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        // MediaPlayer 不在构造期创建：本页作为单例在应用启动时即被解析，
        // 此时 XAML 资源与页面尚未加载完成，过早创建 WinRT 播放器会导致 STATUS_STOWED_EXCEPTION(0xc000027b) 崩溃。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnPageKeyDown;
    }

    /// <summary>短片页视图模型。</summary>
    public ShortViewModel ViewModel { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        // 播放器与背景音乐由 ViewModel 一并释放（含 FFmpeg 解码源）。
        ViewModel.Dispose();

        _player = null;
        PlayerElement.SetMediaPlayer(null);
    }

    /// <summary>惰性创建播放器并绑定到界面元素。</summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player is null)
        {
            _player = new MediaPlayer();
            PlayerElement.SetMediaPlayer(_player);
        }

        return _player;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 取得焦点后键盘输入才会路由到本页；否则空格与方向键会被导航控件接走。
        Focus(FocusState.Programmatic);

        await ViewModel.StartAsync(EnsurePlayer());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    /// <summary>覆盖层按钮就地拦截 Tapped，避免冒泡到舞台触发播放/暂停。</summary>
    private void OnOverlayButtonTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>单击画面切换播放与暂停。</summary>
    private void OnStageTapped(object sender, TappedRoutedEventArgs e) => ViewModel.TogglePlayPause();

    /// <summary>键盘控制：空格播放/暂停，左右方向键切换上一个 / 下一个。</summary>
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePlayPause();
                e.Handled = true;
                break;

            case VirtualKey.Right:
                _ = ViewModel.GoNextAsync();
                e.Handled = true;
                break;

            case VirtualKey.Left:
                _ = ViewModel.GoPreviousAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnToggleMuteClick(object sender, RoutedEventArgs e) => ViewModel.ToggleMute();

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        // 点击到达即留痕：此行缺失 = 按钮点击未触发（UI 层问题），与视图模型层的问题区分开。
        Diagnostics.Log("SHORTUI|next-click");
        _ = ViewModel.GoNextAsync();
    }

    private async void OnViewOriginalClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentItem is null)
        {
            return;
        }

        await App.Services.GetRequiredService<MainWindow>().OpenViewerAsync(ViewModel.CurrentItem);
    }
}
