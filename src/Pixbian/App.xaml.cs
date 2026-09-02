/**
 * 应用程序入口（M2）。
 * 职责：构建依赖注入容器、创建并激活主窗口，在退出时释放容器持有的资源。
 * 复用约定：全部服务与页面统一在 ConfigureServices 中注册，禁止在页面内自行 new 依赖；
 *          数据库与设置在启动阶段完成初始化，长时间运行的索引任务一律由界面触发。
 * 关键约束：数据库初始化与目录创建必须在窗口显示前完成，否则首屏查询会失败；
 *          但不得在此执行全量索引扫描，否则会显著拖长冷启动时间（见 M1 注释）；
 *          Windows App SDK 的自包含引导由 NuGet 包在入口前自动完成，不得手工干预。
 */

using System.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Pixbian.Core.Abstractions;
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
    private static Mutex? _singleInstanceMutex;

    private Window? _window;

    /// <summary>全局服务提供器，供需要解析依赖的界面代码使用。</summary>
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>初始化应用程序对象、构建依赖注入容器并完成数据目录与数据库初始化。</summary>
    public App()
    {
        // 单实例互斥：两个实例同时操作索引库会互相争锁，且文件夹监控会重复建监视器。
        _singleInstanceMutex = new Mutex(true, @"Local\Pixbian-SingleInstance", out var isFirstInstance);

        if (!isFirstInstance)
        {
            NativeMethods.ShowAlreadyRunning();
            Environment.Exit(0);
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;

        AppPaths.EnsureCreated();

        var initializer = new SqliteDatabaseInitializer(AppPaths.DatabasePath);
        initializer.Initialize();

        Services = ConfigureServices(initializer.ConnectionString);
    }

    /// <summary>在应用启动完成时创建并激活主窗口。</summary>
    /// <param name="args">启动参数，当前阶段未使用。</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();

        // 窗口先激活不阻塞首屏，再后台重建磁盘缓存 LRU 表。
        // 【临时诊断】结果落日志：条目数为 0 或抛异常都意味着磁盘层整轮失效。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var count = await Services.GetRequiredService<IThumbnailDiskCache>().InitializeAsync();
            Diagnostics.Log($"DISKINIT|OK|{count}|{stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"DISKINIT|FAIL|{ex.GetType().Name}|{ex.Message}");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 日志写入必须绝对可靠：任何二次异常都会掩盖真正的崩溃原因。
        // 优先写正式日志目录，失败（目录不可写等）回退到临时目录。
        var entry = $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}";

        try
        {
            File.AppendAllText(Path.Combine(AppPaths.LogDirectory, "crash.log"), entry);
        }
        catch (Exception)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "Pixbian-crash.log"), entry);
            }
            catch
            {
                // 已无任何补救手段，放弃记录。
            }
        }

        // 标记已处理，避免应用直接终止；异常内容已落盘可供排查。
        e.Handled = true;
    }

    /// <summary>Win32 互操作：单实例提示。</summary>
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool MessageBoxW(
            nint hWnd,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            string text,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            string caption,
            uint type);

        /// <summary>提示已有实例运行后返回，由调用方退出进程。</summary>
        public static void ShowAlreadyRunning() =>
            MessageBoxW(
                nint.Zero,
                "Pixbian 已在运行，请使用已打开的窗口。",
                "Pixbian",
                0x40);
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

        services.AddSingleton<ISettingsService, JsonSettingsService>();

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
                webSettings.WebSharingPort);
        });

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<GalleryViewModel>();
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ILibraryFolderRepository>(),
            sp.GetRequiredService<IMediaItemRepository>(),
            sp.GetRequiredService<MediaIndexingService>(),
            sp.GetRequiredService<MediaMetadataBackfillService>(),
            sp.GetRequiredService<ISettingsService>(),
            () => sp.GetRequiredService<WebAccessServer>()));
        services.AddSingleton<ImageViewerViewModel>();
        services.AddSingleton<VideoPlayerViewModel>();
        services.AddSingleton<CategoryViewModel>();
        services.AddSingleton<IVideoMetadataReader, VideoMetadataReader>();

        services.AddSingleton<GalleryPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<ImageViewerPage>();
        services.AddSingleton<VideoPlayerPage>();
        services.AddSingleton<CategoryPage>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
