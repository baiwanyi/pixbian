/**
 * Short 页代码后置（短视频视图）。
 * 职责：创建 MediaPlayer 并交给 ViewModel，把单击与键盘输入转换为播放控制，
 *      驱动底部进度条的补间动画，并在离开页面时释放播放器与背景音乐资源。
 * 复用约定：播放器由本页面惰性创建后注入 ViewModel，ViewModel 不负责创建 UI 相关对象。
 * 关键约束：必须在 Unloaded 时释放播放器，否则解码器不会回收，反复进出会导致内存持续增长；
 *          覆盖层按钮（含中央播放按钮）必须就地拦截 Tapped，否则点击会冒泡到舞台，
 *          与 OnStageTapped 叠加成双重播放/暂停切换（即「暂停后无法继续播放」的冲突）；
 *          键盘仅承载方向键与 DEL（经 0 尺寸宿主的全局加速器），播放/暂停只用点击——
 *          加速器要求焦点在页面树内，进入页面与点击画面都会把焦点归位；
 *          主窗口经 App.Services 在需要时解析——直接注入会与「窗口持有本页」形成循环依赖。
 */

using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
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

        // 点击画面任意位置把焦点拉回页面：焦点被导航项等窗口控件持有时，
        // 方向键/DEL 加速器会被其内部按键处理抢先而失效（实测），必须先归位。
        // 只做焦点归位，不标记 Handled、不干扰点击播放/暂停。
        StageRoot.PointerPressed += OnStagePointerPressed;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>短片页视图模型。</summary>
    public ShortViewModel ViewModel { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

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
        // 进入页面必须把焦点拉到页面自身：焦点若留在导航项等窗口控件上，
        // 方向键/DEL 加速器会被其内部按键处理抢先而失效（实测）。
        Focus(FocusState.Programmatic);

        await ViewModel.StartAsync(EnsurePlayer());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    /// <summary>点击画面任意位置：先把焦点归位到页面（保障方向键/DEL 可用），
    /// 随后经 StageRoot.Tapped 切换播放/暂停。</summary>
    private void OnStagePointerPressed(object sender, PointerRoutedEventArgs e) => Focus(FocusState.Programmatic);

    /// <summary>覆盖层按钮就地拦截 Tapped，阻断向舞台冒泡：否则按钮点击会与
    /// 舞台点击叠加成双重播放/暂停切换（即暂停后无法继续播放的冲突来源）。</summary>
    private void OnOverlayButtonTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>单击画面任意位置切换播放与暂停；暂停时中央出现播放按钮，点击它继续播放。</summary>
    private void OnStageTapped(object sender, TappedRoutedEventArgs e) => ViewModel.TogglePlayPause();

    /// <summary>进度属性变化时把进度条补间到新值；值未变不触发（生成属性带相等检查）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShortViewModel.Progress))
        {
            AnimateProgressTo(ViewModel.Progress);
        }
    }

    /// <summary>把进度条平滑补间到目标值：动画时长与 ViewModel 轮询间隔一致，消除逐级跳变。</summary>
    /// <remarks>
    /// 每轮现场创建 Storyboard 并显式设置 From（HoldEnd 复位惯例）；新 Begin 会替换同一
    /// 目标属性上的旧动画，无需挂 Completed 清理。启用依赖动画是 ProgressBar.Value 所必需。
    /// </remarks>
    private void AnimateProgressTo(double target)
    {
        var animation = new DoubleAnimation
        {
            From = ProgressFill.Value,
            To = target,
            Duration = new Duration(ShortViewModel.ProgressInterval),
            EnableDependentAnimation = true
        };

        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, ProgressFill);
        Storyboard.SetTargetProperty(animation, "Value");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>中央播放按钮：暂停态点击继续播放；Tapped 已拦截，不与舞台点击叠加。</summary>
    private void OnPlayOverlayClick(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause();

    /// <summary>键盘加速器统一入口：左右方向键切换上/下一个，DEL 删除当前视频。</summary>
    /// <remarks>
    /// 播放/暂停不在此列——空格加速器在焦点被窗口控件持有时不可靠（多轮实测），已放弃，
    /// 播放/暂停唯一入口是点击画面与中央播放按钮。触发即标记 Handled：
    /// 菜单项上的 DEL 与宿主加速器靠它互斥，不产生双重执行。
    /// </remarks>
    private void OnAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        switch (args.KeyboardAccelerator.Key)
        {
            case VirtualKey.Right:
                _ = ViewModel.GoNextAsync();
                break;

            case VirtualKey.Left:
                _ = ViewModel.GoPreviousAsync();
                break;

            case VirtualKey.Delete:
                ViewModel.DeleteCurrent();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    private void OnToggleMuteClick(object sender, RoutedEventArgs e) => ViewModel.ToggleMute();

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e) => _ = ViewModel.ToggleFavoriteAsync();

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        // 菜单关闭后焦点会回到锚定按钮，空格将再次触发它——先归位再执行删除。
        Focus(FocusState.Programmatic);
        ViewModel.DeleteCurrent();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.GoNextAsync();
    }

    private async void OnViewOriginalClick(object sender, RoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);

        if (ViewModel.CurrentItem is null)
        {
            return;
        }

        await App.Services.GetRequiredService<MainWindow>().OpenViewerAsync(ViewModel.CurrentItem);
    }
}
