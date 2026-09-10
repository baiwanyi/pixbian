/**
 * 设置页代码后置——偏好项（partial）。
 * 职责：主题、查看器（滚轮行为 / 缩放首选项）、幻灯片（播放顺序 / 切换模式 / 间隔 /
 *      片段时长上限 / 背景音乐与音量 / 动画 / 完整视频 / 静音播放 / 背景虚化）的
 *      控件回填与落盘。
 * 复用约定：全部经 SettingsViewModel 的属性 setter 落盘（内部广播通知外壳）；
 *          片段档位取值复用 ClipRangePlanner.PresetOptions，与短片页裁决规则同源。
 * 关键约束：NumberBox 清空输入时 Value 为 NaN 而非 0，写回前必须拦截（此处按百分比
 *          去重拦截）；开关不走双向绑定（绑定回写会让点击变迟钝），故回填后必须由
 *          SyncToggleStateLabels 统一刷新状态文字；下拉索引为 int 对 int，变更即落盘。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>设置页的偏好项。</summary>
public sealed partial class SettingsPage
{
    private int _themeIndex;
    private int _wheelModeIndex;
    private int _initialZoomIndex;
    private int _playOrderIndex;
    private int _transitionIndex;
    private int _backgroundMusicIndex;
    private int _bgmVolumePercent;
    private int _clipPresetIndex;
    private int _slideIntervalIndex;

    /// <summary>幻灯片间隔下拉的可选秒数（与选项顺序一致）。</summary>
    private static readonly int[] SlideIntervalOptions = { 1, 3, 5, 10, 20, 30, 60 };

    /// <summary>片段时长上限下拉的可选秒数（与共享裁决器保持一致）。</summary>
    private static readonly int[] ClipPresetOptions = ClipRangePlanner.PresetOptions;

    /// <summary>主题选择器的当前索引。</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetField(ref _themeIndex, value);
    }

    /// <summary>鼠标滚轮行为选择器的当前索引。</summary>
    public int WheelModeIndex
    {
        get => _wheelModeIndex;
        set => SetField(ref _wheelModeIndex, value);
    }

    /// <summary>缩放首选项选择器的当前索引。</summary>
    public int InitialZoomIndex
    {
        get => _initialZoomIndex;
        set => SetField(ref _initialZoomIndex, value);
    }

    /// <summary>幻灯片播放顺序选择器的当前索引。</summary>
    public int PlayOrderIndex
    {
        get => _playOrderIndex;
        set => SetField(ref _playOrderIndex, value);
    }

    /// <summary>幻灯片切换模式选择器的当前索引。</summary>
    public int TransitionIndex
    {
        get => _transitionIndex;
        set => SetField(ref _transitionIndex, value);
    }

    /// <summary>片段时长上限选择器的当前索引。</summary>
    public int ClipPresetIndex
    {
        get => _clipPresetIndex;
        set => SetField(ref _clipPresetIndex, value);
    }

    /// <summary>幻灯片间隔选择器的当前索引。</summary>
    public int SlideIntervalIndex
    {
        get => _slideIntervalIndex;
        set => SetField(ref _slideIntervalIndex, value);
    }

    /// <summary>背景音乐模式选择器的当前索引。</summary>
    public int BackgroundMusicIndex
    {
        get => _backgroundMusicIndex;
        set => SetField(ref _backgroundMusicIndex, value);
    }

    /// <summary>背景音乐音量的百分比显示文本。</summary>
    public string BgmVolumeText => $"{_bgmVolumePercent}%";

    /// <summary>按当前设置同步幻灯片区块的控件状态。</summary>
    /// <remarks>
    /// 间隔不做双向绑定：NumberBox.Value 是 double 而设置项是 int，绑定会在用户键入中间态
    /// （如刚敲下「3」准备输「30」）就写盘，产生多余 IO。改由 ValueChanged 事件落盘，
    /// 此处只负责进入页面时的回填；两个下拉的索引是 int 对 int，可安全双向绑定，
    /// 这里重复赋值只为覆盖其他入口改过设置的情形，SetField 会自行判等。
    /// </remarks>
    private void SyncSlideShowControls()
    {
        SlideIntervalSelector.SelectedIndex =
            Math.Max(0, Array.IndexOf(SlideIntervalOptions, ViewModel.SlideShowIntervalSeconds));
        PlayOrderIndex = (int)ViewModel.SlideShowOrder;
        TransitionIndex = (int)ViewModel.SlideShowTransition;
        ThemeIndex = (int)ViewModel.Theme;
        WheelModeIndex = (int)ViewModel.ViewerWheelMode;
        InitialZoomIndex = (int)ViewModel.ViewerInitialZoom;
        BackgroundMusicIndex = (int)ViewModel.SlideShowBackgroundMusic;
        BgmVolumeSlider.Value = ViewModel.SlideShowBackgroundMusicVolume * 100;
        ClipPresetSelector.SelectedIndex =
            Array.IndexOf(ClipPresetOptions, ViewModel.SlideShowClipPresetSeconds);

        // 开关不走绑定：视觉切换完全由用户交互驱动（回写绑定会造成点击迟钝），此处只做回填。
        IncludeVideosToggle.IsOn = ViewModel.SlideShowFullVideoPlayback;
        VideoMutedToggle.IsOn = ViewModel.SlideShowSilentPlayback;
        BlurBackdropToggle.IsOn = ViewModel.SlideShowBlurBackdrop;
        AnimationToggle.IsOn = ViewModel.SlideShowAnimationEnabled;

        // 回填会触发 Toggled 事件，状态文字必须在此统一刷新（事件路径上 x:Bind 通知不可靠）。
        SyncToggleStateLabels();
    }

    /// <summary>完整视频开关：切换即落盘，放映下次装载视频时按新策略计算区间。</summary>
    private void OnFullVideoToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowFullVideoPlayback = IncludeVideosToggle.IsOn;
    }

    /// <summary>静音播放开关：切换即落盘，放映经 ApplySettings 推送即时重算音频策略。</summary>
    private void OnVideoMutedToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowSilentPlayback = VideoMutedToggle.IsOn;
    }

    /// <summary>背景虚化开关：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnBlurBackdropToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowBlurBackdrop = BlurBackdropToggle.IsOn;
    }

    /// <summary>背景音乐模式：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnBackgroundMusicSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SlideShowBackgroundMusic = (BackgroundMusicMode)BackgroundMusicSelector.SelectedIndex;

    /// <summary>画面动画开关：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnAnimationToggled(object sender, RoutedEventArgs e)
    {
        SyncToggleStateLabels();
        ViewModel.SlideShowAnimationEnabled = AnimationToggle.IsOn;
    }

    /// <summary>片段时长上限：切换即落盘，放映下次装载视频时按新档位计算区间。</summary>
    private void OnClipPresetSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SlideShowClipPresetSeconds = ClipPresetOptions[ClipPresetSelector.SelectedIndex];

    /// <summary>幻灯片间隔：切换即落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnSlideIntervalSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SlideShowIntervalSeconds = SlideIntervalOptions[SlideIntervalSelector.SelectedIndex];

    /// <summary>背景音乐音量：百分比换算为 0–1 落盘，放映经 ApplySettings 推送即时生效。</summary>
    private void OnBgmVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var percent = (int)Math.Round(e.NewValue);

        if (_bgmVolumePercent == percent)
        {
            return;
        }

        _bgmVolumePercent = percent;
        ViewModel.SlideShowBackgroundMusicVolume = percent / 100.0;
        OnPropertyChanged(nameof(BgmVolumeText));
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.Theme = (AppTheme)ThemeSelector.SelectedIndex;
    }

    /// <summary>鼠标滚轮行为变更即落盘。</summary>
    private void OnWheelModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WheelModeSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ViewerWheelMode = (ViewerWheelMode)WheelModeSelector.SelectedIndex;
    }

    /// <summary>缩放首选项变更即落盘。</summary>
    private void OnInitialZoomSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InitialZoomSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.ViewerInitialZoom = (ViewerInitialZoom)InitialZoomSelector.SelectedIndex;
    }

    /// <summary>幻灯片播放顺序变更即落盘。</summary>
    private void OnPlayOrderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlayOrderSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.SlideShowOrder = (SlideShowPlayOrder)PlayOrderSelector.SelectedIndex;
    }

    /// <summary>幻灯片切换模式变更即落盘。</summary>
    private void OnTransitionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransitionSelector.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.SlideShowTransition = (SlideShowTransitionMode)TransitionSelector.SelectedIndex;
    }
}
