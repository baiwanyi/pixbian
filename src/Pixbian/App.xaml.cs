/**
 * 应用程序入口（M2）。
 * 职责：构建依赖注入容器、创建并激活主窗口，并承担单实例收口与文件激活分发。
 * 复用约定：全部服务与页面统一在 ConfigureServices 中注册，禁止在页面内自行 new 依赖；
 *          数据库在构造阶段完成初始化，设置由外壳在窗口显示后加载，
 *          长时间运行的索引任务一律由界面触发；
 *          单实例走 Windows App SDK 的 AppInstance 键注册，取代早期的进程互斥量。
 * 关键约束：数据库初始化与目录创建必须在窗口显示前完成，否则首屏查询会失败；
 *          但不得在此执行全量索引扫描，否则会显著拖长冷启动时间（见 M1 注释）；
 *          非主实例一律不初始化数据库与依赖容器，只把激活重定向给主实例后退出，
 *          否则两个进程会争抢同一个索引库并重复建立文件夹监视器；
 *          图片文件激活走轻量预览：不创建主窗口，只开查看器窗口，查看器关闭即退出进程；
 *          文件激活须延后一拍再打开，否则会与首屏缩略图解码争抢 UI 线程；
 *          Windows App SDK 的自包含引导由 NuGet 包在入口前自动完成，不得手工干预。
 */

using System.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
// 只取文件激活接口：整命名空间导入会让 LaunchActivatedEventArgs 在
// Microsoft.UI.Xaml 与 Windows.ApplicationModel.Activation 之间产生二义性。
using IFileActivatedEventArgs = Windows.ApplicationModel.Activation.IFileActivatedEventArgs;
// 域级未处理异常与 XAML 的 UnhandledExceptionEventArgs 同名，显式消歧（CS0104）。
using UnhandledExceptionEventArgs = System.UnhandledExceptionEventArgs;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
using Pixbian.Core.Utilities;
using Pixbian.Data.Repositories;
using Pixbian.Data.Sqlite;
using Pixbian.Imaging.Services;
using Pixbian.Media.Services;
using Pixbian.Services;
using Pixbian.ViewModels;
using Pixbian.Views;
using Pixbian.WebServer;

namespace Pixbian;

/// <summary>Pixbian 应用程序对象。</summary>
public partial class App : Application
{
    /// <summary>单实例键：进程间识别主实例用，与稀疏包的 Identity.Name 无关联。</summary>
    private const string SingleInstanceKey = "Pixbian-Main";

    private AppInstance? _mainInstance;

    private Window? _window;

    /// <summary>轻量预览模式的查看器窗口（未创建主窗口时承载双击打开的图片）。</summary>
    private ImageViewerWindow? _lightweightViewer;

    /// <summary>全局服务提供器，供需要解析依赖的界面代码使用。</summary>
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>初始化应用程序对象、构建依赖注入容器并完成数据目录与数据库初始化。</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        _mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        // 非主实例：不建库也不建容器，由 OnLaunched 把激活转交主实例后退出。
        if (!_mainInstance.IsCurrent)
        {
            return;
        }

        AppPaths.EnsureCreated();

        // 全局异常的两条兜底通道：XAML 的 UnhandledException 只覆盖 UI 线程，
        // 后台线程与未观察的任务异常若不在此落盘，往往表现为「卡住/闪退但 crash.log 无痕」。
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var initializer = new SqliteDatabaseInitializer(AppPaths.DatabasePath);
        initializer.Initialize();

        Services = ConfigureServices(initializer.ConnectionString);
    }

    // —— 按钮悬停 / 按下底色（全应用统一管理入口） ——
    // 浅色主题的系统默认悬停底色（约 6% 黑）肉眼不可见，且实测经主题字典覆盖
    // （Default / Light 键）在浅色下不生效——浅色解析先按 Light 键命中框架合并字典后直接返回，
    // 不 fallback 到应用层 Default。故改为代码按主题向资源字典写入系统键直接键
    // （查找优先级最高），刷新时机：启动、设置切换主题、跟随系统主题反转。
    // 自定义模板按钮（标题栏设置按钮）不引用系统键，改用语义键 PixbianButtonHoverBrush /
    // PixbianButtonPressedBrush（App.xaml 主题字典定义，随主题自动切换，无需代码刷新）。
    // 两类键色值保持同值同语义；色值调整只改本处常量。

    /// <summary>浅色悬停底色 alpha（6% 黑，与标题栏设置按钮原悬停色一致）。</summary>
    private const byte HoverAlphaLight = 0x0F;

    /// <summary>浅色按下底色 alpha（8% 黑，比悬停深一档；本应用按下反馈仅靠加深）。</summary>
    private const byte PressedAlphaLight = 0x14;

    /// <summary>深色悬停底色 alpha（10% 白；系统 Subtle 令牌仅 6% 白，深色下不可见）。</summary>
    private const byte HoverAlphaDark = 0x1A;

    /// <summary>深色按下底色 alpha（15% 白）。</summary>
    private const byte PressedAlphaDark = 0x26;

    /// <summary>悬停底色资源键（Button 与 ToggleButton 各一）。</summary>
    private static readonly string[] HoverBrushKeys =
        ["ButtonBackgroundPointerOver", "ToggleButtonBackgroundPointerOver"];

    /// <summary>按下底色资源键（Button 与 ToggleButton 各一）。</summary>
    private static readonly string[] PressedBrushKeys =
        ["ButtonBackgroundPressed", "ToggleButtonBackgroundPressed"];

    /// <summary>按当前主题刷新全应用按钮悬停 / 按下底色（含 ToggleButton）。</summary>
    /// <param name="isDark">当前是否深色主题。</param>
    public static void ApplyButtonHoverBrushes(bool isDark)
    {
        var hover = new SolidColorBrush(isDark
            ? Microsoft.UI.ColorHelper.FromArgb(HoverAlphaDark, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(HoverAlphaLight, 0x00, 0x00, 0x00));
        var pressed = new SolidColorBrush(isDark
            ? Microsoft.UI.ColorHelper.FromArgb(PressedAlphaDark, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(PressedAlphaLight, 0x00, 0x00, 0x00));

        foreach (var key in HoverBrushKeys)
        {
            Current.Resources[key] = hover;
        }

        foreach (var key in PressedBrushKeys)
        {
            Current.Resources[key] = pressed;
        }
    }

    /// <summary>在应用启动完成时按需创建主窗口或轻量预览窗口，并处理本次激活参数。</summary>
    /// <param name="args">启动参数，当前阶段未使用。</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_mainInstance is null)
        {
            return;
        }

        // 已有实例在运行：把本次激活（含双击关联文件）转交给它，本进程不再建窗口。
        if (!_mainInstance.IsCurrent)
        {
            await _mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Environment.Exit(0);
            return;
        }

        // 后续实例被重定向过来的激活在主实例里继续处理（例如再双击另一个文件）。
        _mainInstance.Activated += OnInstanceActivated;

        // 先按用户设置的主题刷新按钮悬停底色：必须在任何按钮渲染前写入，保证首屏悬停即可见。
        // 设置在窗口显示后才从磁盘加载，此处读 Current（未加载即默认值，与首屏主题一致）。
        var theme = Services.GetRequiredService<ISettingsService>().Current.Theme;
        ApplyButtonHoverBrushes(theme == AppTheme.Dark);

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // 双击关联的图片文件走轻量预览：不建主窗口，只开查看器窗口。
        if (TryOpenLightweightViewer(activation))
        {
            return;
        }

        await ShowMainWindowAsync();

        // 处理本次启动的激活参数：双击关联文件时为文件激活，直接打开该文件。
        HandleActivation(activation);
    }

    /// <summary>创建并激活主窗口，随后恢复磁盘缓存与音乐库；已存在主窗口时直接返回。</summary>
    private async Task ShowMainWindowAsync()
    {
        if (_window is not null)
        {
            return;
        }

        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();

        // 窗口先激活不阻塞首屏，再后台重建磁盘缓存 LRU 表；失败静默（缓存层自愈为重新编码）。
        try
        {
            await Services.GetRequiredService<IThumbnailDiskCache>().InitializeAsync();
        }
        catch (Exception)
        {
            // 磁盘缓存初始化失败不影响首屏：后续读取按未命中处理。
        }

        // 音乐库曲目：从索引库直接恢复到内存，避免每次启动都递归扫描音乐目录
        // （机械盘上扫描上千文件耗时可达秒级）。失败不影响首屏，短片页退化为无背景音乐。
        try
        {
            await Services.GetRequiredService<IMusicLibraryService>().LoadAsync();
        }
        catch (Exception)
        {
            // 音乐库加载失败仅影响短片页背景音乐，静默降级。
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        // 标记已处理，避免应用直接终止；异常内容已落盘可供排查。
        e.Handled = true;
    }

    /// <summary>把异常追加到应用日志：日志写入必须绝对可靠，任何二次异常都会掩盖真正的崩溃原因。</summary>
    /// <param name="exception">待记录的异常。</param>
    private static void WriteCrashLog(Exception exception)
    {
        // 统一走 AppLog：自带目录创建、大小滚动与归档，不再各自拼路径与格式。
        AppLog.Error("Crash", "未处理的异常。", exception);
    }

    /// <summary>非 UI 线程未处理异常的兜底：进程即将终止，此处只能落盘。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">异常参数。</param>
    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("Domain", "非 UI 线程未处理异常。", exception);
        }
    }

    /// <summary>未观察的任务异常的兜底：不记录时 fire-and-forget 的失败会彻底消失。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">异常参数。</param>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Task", "未观察的任务异常。", e.Exception);

        // 已记录即视为已观察，避免终结线程再次抛出导致进程终止。
        e.SetObserved();
    }

    /// <summary>后续实例把激活重定向过来时，在主实例中继续处理。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">被重定向的激活参数。</param>
    private void OnInstanceActivated(object? sender, AppActivationArguments e) => HandleActivation(e);

    /// <summary>处理激活参数：图片文件激活走轻量预览，其余交给主窗口打开。</summary>
    /// <param name="activationArgs">激活参数；非文件激活、无文件或窗口未就绪时直接返回。</param>
    private void HandleActivation(AppActivationArguments activationArgs)
    {
        if (_window is null)
        {
            // 轻量预览模式：图片只开查看器窗口；视频与普通启动仍需主窗口宿主，
            // 先建主窗口再分发，否则这类激活在本进程里没有任何界面响应。
            if (TryOpenLightweightViewer(activationArgs))
            {
                return;
            }

            _ = ShowMainWindowThenActivateAsync(activationArgs);
            return;
        }

        if (activationArgs.Kind != ExtendedActivationKind.File)
        {
            return;
        }

        if (_window is not MainWindow window
            || activationArgs.Data is not IFileActivatedEventArgs fileArgs
            || fileArgs.Files.Count == 0)
        {
            return;
        }

        var path = fileArgs.Files[0].Path;

        // 延后一拍再打开：本方法可能在窗口尚未完成首帧布局时执行，
        // 立即打开查看器或播放器会与首屏缩略图解码争抢 UI 线程。
        window.DispatcherQueue.TryEnqueue(() => _ = window.OpenFileAsync(path));
    }

    /// <summary>轻量预览实例收到需要主窗口的激活（视频文件或普通启动）：先建主窗口再分发。</summary>
    /// <param name="activationArgs">激活参数。</param>
    private async Task ShowMainWindowThenActivateAsync(AppActivationArguments activationArgs)
    {
        try
        {
            await ShowMainWindowAsync();
            HandleActivation(activationArgs);
        }
        catch (Exception exception)
        {
            // 事件处理器里的 fire-and-forget：异常必须落盘，否则表现为「点了没反应」。
            WriteCrashLog(exception);
        }
    }

    /// <summary>尝试以轻量预览方式打开双击的图片文件：只开查看器窗口，不创建主窗口。</summary>
    /// <param name="activation">激活参数。</param>
    /// <returns>已按轻量方式打开时为 true；非图片文件激活或主窗口已存在时为 false，交由常规流程处理。</returns>
    private bool TryOpenLightweightViewer(AppActivationArguments activation)
    {
        if (_window is not null || activation.Kind != ExtendedActivationKind.File)
        {
            return false;
        }

        if (activation.Data is not IFileActivatedEventArgs fileArgs || fileArgs.Files.Count == 0)
        {
            return false;
        }

        var path = fileArgs.Files[0].Path;

        if (!MediaFileClassifier.IsSupported(path))
        {
            return false;
        }

        var item = UnindexedMediaItemFactory.Create(path);

        // 视频仍交主窗口：播放器页依赖主窗口的播放态宿主与标题栏放行区域，暂无独立宿主。
        if (item is null || item.Kind != MediaKind.Image)
        {
            return false;
        }

        var viewer = _lightweightViewer ??= CreateLightweightViewer();

        viewer.Activate();
        viewer.ViewerPage.BeginOpen();

        // 延后一拍再加载：窗口刚创建时尚未完成首帧布局，立即解码会与之争抢 UI 线程。
        viewer.DispatcherQueue.TryEnqueue(() => _ = LoadLightweightViewerAsync(item));

        return true;
    }

    /// <summary>创建轻量预览的查看器窗口，并收口「查看器关闭即退出进程」的生命周期。</summary>
    private ImageViewerWindow CreateLightweightViewer()
    {
        var viewer = Services.GetRequiredService<ImageViewerWindow>();

        // 轻量模式没有主窗口应用主题，此处按用户设置给页面根指定一次。
        viewer.ViewerPage.RequestedTheme = MapTheme(Services.GetRequiredService<ISettingsService>().Current.Theme);

        viewer.Closed += (_, _) =>
        {
            _lightweightViewer = null;

            // 轻量模式没有主窗口：查看器一关进程就没有任何界面，必须退出，
            // 否则进程常驻后台，之后从开始菜单启动会被单实例重定向到这个无窗口实例。
            if (_window is null)
            {
                Environment.Exit(0);
            }
        };

        return viewer;
    }

    /// <summary>把文件加载进轻量预览的查看器；失败落盘留痕，不影响退出判断。</summary>
    /// <param name="item">未入库的图片条目。</param>
    private static async Task LoadLightweightViewerAsync(MediaItem item)
    {
        try
        {
            await Services.GetRequiredService<ImageViewerViewModel>().LoadPlaylistAsync([item], 0);
        }
        catch (Exception exception)
        {
            // fire-and-forget 里的异常是黑洞：轻量模式无主窗口承载提示，只能落盘。
            WriteCrashLog(exception);
        }
    }

    /// <summary>注册全部服务、视图模型与页面。</summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    private static ServiceProvider ConfigureServices(string connectionString)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions
        {
            // 缩略图缓存按字节数限额（条目 Size 为位图估算字节数）：解码档位统一为 512
            // （低档视图复用同一成品，显示端缩小），单条约 1MB，200 MB 限额下约 200 条常驻
            // ——位图长期驻留会推高 GC 与工作集，实测随浏览累积出现界面渐缓与未响应。
            SizeLimit = 200L * 1024 * 1024
        }));

        // 设置服务注入 DPAPI 保护器：Web 密码哈希落盘前加密，旧版明文哈希在下次保存时自动升级。
        services.AddSingleton<ISettingsService>(_ => new JsonSettingsService(hashProtector: new DpapiHashProtector()));

        // 缩略图磁盘缓存：LRU 2 GB（约数千条 512px 以下成品字节），命中即跳过全量解码。
        services.AddSingleton<IThumbnailDiskCache>(_ => new ThumbnailDiskCache(
            AppPaths.ThumbnailCacheDirectory,
            2L * 1024 * 1024 * 1024));

        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IImageMetadataReader, ImageMetadataReader>();
        services.AddSingleton<IImageEditService, ImageEditService>();
        services.AddSingleton<IMediaItemRepository>(_ => new SqliteMediaItemRepository(connectionString));
        services.AddSingleton<ILibraryFolderRepository>(_ => new SqliteLibraryFolderRepository(connectionString));
        services.AddSingleton<ICategoryRepository>(_ => new SqliteCategoryRepository(connectionString));
        services.AddSingleton<ICategoryRuleRepository>(_ => new SqliteCategoryRuleRepository(connectionString));
        services.AddSingleton<IMusicTrackRepository>(_ => new SqliteMusicTrackRepository(connectionString));
        services.AddSingleton<IFavoriteGroupRepository>(_ => new SqliteFavoriteGroupRepository(connectionString));
        services.AddSingleton<MediaIndexingService>();
        services.AddSingleton<IMediaMetadataProbe, MediaMetadataProbe>();
        services.AddSingleton<MediaMetadataBackfillService>();

        // Web 服务器为单例，但工厂延迟到首次解析时执行——
        // 此时 ShellViewModel.InitializeAsync 已从磁盘加载设置，Current 即为真实值。
        services.AddSingleton(sp =>
        {
            var webSettings = sp.GetRequiredService<ISettingsService>().Current;

            return new WebAccessServer(
                sp.GetRequiredService<IMediaItemRepository>(),
                sp.GetRequiredService<ILibraryFolderRepository>(),
                webSettings.WebPasswordHash,
                webSettings.WebSharingPort,
                new FileLogger(nameof(WebAccessServer)),
                webSettings.WebSharingBindAddress);
        });

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IRecycleBinService, RecycleBinService>();
        services.AddSingleton<GalleryViewModel>();
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ILibraryFolderRepository>(),
            sp.GetRequiredService<IMediaItemRepository>(),
            sp.GetRequiredService<MediaIndexingService>(),
            sp.GetRequiredService<MediaMetadataBackfillService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IMusicLibraryService>(),
            () => sp.GetRequiredService<WebAccessServer>()));
        services.AddSingleton<ImageViewerViewModel>();
        services.AddSingleton<SlideShowViewModel>();
        services.AddSingleton<VideoPlayerViewModel>();
        services.AddSingleton<CategoryViewModel>();
        services.AddSingleton<FavoriteGroupViewModel>();
        services.AddSingleton<ShortViewModel>();
        services.AddSingleton<IVideoMetadataReader, VideoMetadataReader>();
        services.AddSingleton<IVideoPlaybackItemFactory, VideoPlaybackItemFactory>();
        services.AddSingleton<IMusicLibraryService, MusicLibraryService>();

        services.AddSingleton<GalleryPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<ImageViewerPage>();
        services.AddTransient<ImageViewerWindow>();
        services.AddSingleton<SlideShowPage>();
        services.AddTransient<SlideShowWindow>();
        services.AddSingleton<VideoPlayerPage>();
        services.AddSingleton<CategoryPage>();
        services.AddSingleton<ShortPage>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>把领域层的主题枚举映射为 WinUI 的主题枚举（主窗口与轻量预览窗口共用）。</summary>
    /// <param name="theme">领域层主题。</param>
    public static ElementTheme MapTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };
}
