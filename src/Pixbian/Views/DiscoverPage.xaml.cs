/**
 * 发现模式页代码后置（M6）。
 * 职责：提供间隔文本、范围索引与播放图标，处理键盘快捷键，并在离开页面时停止自动播放。
 * 复用约定：视图模型由依赖注入提供；范围下拉框用索引与枚举互转，避免 XAML 直接绑定枚举。
 * 关键约束：离开页面必须停止定时器，否则后台会持续触发切换并持续解码缩略图；
 *          键盘快捷键依赖页面获得焦点，故在 Loaded 时主动请求焦点。
 */

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.Core.Services;
using Pixbian.ViewModels;
using Windows.System;

namespace Pixbian.Views;

/// <summary>发现模式页。</summary>
public sealed partial class DiscoverPage : Page, INotifyPropertyChanged
{
    private int _scopeIndex;

    /// <summary>初始化发现页。</summary>
    /// <param name="viewModel">发现视图模型，由依赖注入提供。</param>
    public DiscoverPage(DiscoverViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        IntervalTexts = DiscoverViewModel.IntervalOptions
            .Select(seconds => string.Create(CultureInfo.CurrentCulture, $"{seconds} 秒"))
            .ToList();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnKeyDown;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>发现视图模型。</summary>
    public DiscoverViewModel ViewModel { get; }

    /// <summary>间隔选项的展示文本，如「5 秒」；取自视图模型，避免两处硬编码不同步。</summary>
    public IReadOnlyList<string> IntervalTexts { get; }

    /// <summary>范围下拉框的当前索引。</summary>
    public int ScopeIndex
    {
        get => _scopeIndex;
        set
        {
            if (_scopeIndex == value)
            {
                return;
            }

            _scopeIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>播放按钮图标。</summary>
    public string PlayPauseGlyph => ViewModel.IsPlaying ? "\uE769" : "\uE768";

    /// <summary>播放按钮文本。</summary>
    public string PlayPauseText => ViewModel.IsPlaying ? "停止" : "自动播放";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // 离开页面必须停止定时器，否则后台会持续触发切换。
        ViewModel.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoverViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(PlayPauseGlyph));
            OnPropertyChanged(nameof(PlayPauseText));
        }
    }

    private void OnScopeChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e) =>
        _ = ViewModel.SetScopeAsync(ScopeIndex switch
        {
            1 => DiscoverScope.Images,
            2 => DiscoverScope.Videos,
            _ => DiscoverScope.All
        });

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                await ViewModel.NextAsync();
                e.Handled = true;
                break;

            case VirtualKey.F:
                await ViewModel.FavoriteAsync();
                e.Handled = true;
                break;

            case VirtualKey.X:
                await ViewModel.SkipAsync();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                ViewModel.Stop();
                e.Handled = true;
                break;
        }
    }

    private void OnTogglePlayClick(object sender, RoutedEventArgs e) => ViewModel.TogglePlay();

    private async void OnNextClick(object sender, RoutedEventArgs e) => await ViewModel.NextAsync();

    private async void OnFavoriteClick(object sender, RoutedEventArgs e) =>
        await ViewModel.FavoriteAsync();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
