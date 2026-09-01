/**
 * 应用外壳视图模型（M2）。
 * 职责：驱动左侧导航的项目切换、搜索关键词的防抖下发，以及主题与视图设置的统一应用。
 * 复用约定：导航目标以字符串标记区分，由界面层的 NavigationView 负责映射到具体页面；
 *          搜索输入不走「每敲一个字符就查库」，而是经过防抖窗口后才真正下发查询。
 * 关键约束：ApplySettings 是主题与视图配置的唯一入口，界面各页面不得各自读取设置，
 *          否则会出现「改了设置但部分页面不刷新」的一致性问题。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;

namespace Pixbian.ViewModels;

/// <summary>导航项标记。</summary>
public enum NavigationTarget
{
    /// <summary>全部照片。</summary>
    AllPhotos = 0,

    /// <summary>视频。</summary>
    Videos = 1,

    /// <summary>设置。</summary>
    Settings = 2,

    /// <summary>收藏夹。</summary>
    Favorites = 3
}

/// <summary>应用外壳视图模型。</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private const int SearchDebounceMilliseconds = 350;

    private readonly ISettingsService _settings;
    private CancellationTokenSource? _searchDebounceCts;

    [ObservableProperty]
    private NavigationTarget _currentTarget = NavigationTarget.AllPhotos;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _thumbnailSize = ThumbnailSizes.Default;

    [ObservableProperty]
    private GalleryViewMode _viewMode = GalleryViewMode.Justified;

    public ShellViewModel(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>当前设置快照。</summary>
    public AppSettings Settings => _settings.Current;

    /// <summary>导航目标发生变化。</summary>
    public event EventHandler<NavigationTarget>? TargetChanged;

    /// <summary>搜索关键词经防抖后确认。</summary>
    public event EventHandler<string>? SearchSubmitted;

    /// <summary>设置发生变化，需要各页面重新应用配置。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>加载设置并应用到外壳。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _settings.LoadAsync(cancellationToken);
        ApplySettings(_settings.Current);
    }

    /// <summary>统一应用设置到本地状态并广播变更。</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ThumbnailSize = settings.ThumbnailSize;
        ViewMode = settings.ViewMode;

        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>切换导航目标。</summary>
    /// <param name="target">目标页面。</param>
    public void NavigateTo(NavigationTarget target)
    {
        if (CurrentTarget == target)
        {
            return;
        }

        CurrentTarget = target;
        TargetChanged?.Invoke(this, target);
    }

    /// <summary>搜索关键词变化（防抖后下发）。</summary>
    /// <param name="text">输入文本。</param>
    public async Task OnSearchTextChangedAsync(string text)
    {
        SearchText = text ?? string.Empty;

        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(SearchDebounceMilliseconds, _searchDebounceCts.Token);
            SearchSubmitted?.Invoke(this, SearchText);
        }
        catch (OperationCanceledException)
        {
            // 用户仍在继续输入，等待下一次防抖窗口结束。
        }
    }

    /// <summary>保存当前设置。</summary>
    /// <param name="settings">新设置。</param>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _settings.SaveAsync(settings);
        ApplySettings(settings);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
    }
}
