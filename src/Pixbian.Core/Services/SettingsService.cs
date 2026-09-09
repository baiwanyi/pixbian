/**
 * 用户设置服务的抽象与 JSON 实现（M2）。
 * 职责：持久化并读取界面偏好设置，供界面层在启动时恢复用户上次的配置。
 * 复用约定：序列化使用 System.Text.Json；路径统一取自 AppPaths，便于迁移与备份；
 *          敏感字段（Web 密码哈希）的保护经 IHashProtector 注入（如 DPAPI），本层不感知具体实现。
 * 关键约束：读取失败（文件损坏、权限不足、首次运行）必须降级为默认设置而非抛异常，
 *          否则设置文件损坏会导致应用彻底无法启动；
 *          解除保护失败时必须把哈希置空而非带病运行——无效哈希会让 Web 访问永远无法登录；
 *          旧版明文哈希原样加载、下次保存时自动升级为受保护格式，保证平滑迁移；
 *          写入采用「先写临时文件再原子替换」策略，避免写入中途崩溃留下半截文件。
 */

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pixbian.Core.Models;
using Pixbian.Core.Utilities;

namespace Pixbian.Core.Abstractions;

/// <summary>设置文件敏感字段保护器。</summary>
public interface IHashProtector
{
    /// <summary>判断给定值是否已是受保护格式。</summary>
    /// <param name="value">待判断的值。</param>
    bool IsProtected(string value);

    /// <summary>保护明文值。</summary>
    /// <param name="plaintext">明文。</param>
    /// <returns>受保护格式（自带前缀标记）。</returns>
    string Protect(string plaintext);

    /// <summary>解除保护。</summary>
    /// <param name="protectedValue">受保护格式。</param>
    /// <returns>明文。</returns>
    string Unprotect(string protectedValue);
}

/// <summary>用户设置服务。</summary>
public interface ISettingsService
{
    /// <summary>当前设置；未加载时为默认值。</summary>
    AppSettings Current { get; }

    /// <summary>从磁盘加载设置；失败时保留默认值。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存设置到磁盘。</summary>
    /// <param name="settings">待保存的设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>基于 JSON 文件的设置服务。</summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // 枚举以名称存取（如 "dark"），设置文件人类可读；
        // 未配置此转换器时字符串无法映射到枚举，整个文件会解析失败并静默回退默认值。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _settingsPath;
    private readonly IHashProtector? _hashProtector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppSettings _current = new();

    /// <summary>初始化设置服务。</summary>
    /// <param name="settingsPath">设置文件路径；为空时使用 AppPaths 中的默认位置。</param>
    /// <param name="hashProtector">敏感字段保护器；为空时不启用保护（测试或显式关闭）。</param>
    public JsonSettingsService(string? settingsPath = null, IHashProtector? hashProtector = null)
    {
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
        _hashProtector = hashProtector;
        var directory = Path.GetDirectoryName(_settingsPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    public AppSettings Current => _current;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_settingsPath))
            {
                _current = new AppSettings();
                return;
            }

            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken)
                .ConfigureAwait(false);

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);

            if (loaded is not null)
            {
                _current = Normalize(UnprotectHash(loaded));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 设置文件损坏或不可读时降级为默认值，保证应用仍可启动。
            _current = new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>解除 Web 密码哈希的保护；失败时置空，避免无效哈希让 Web 访问永远无法登录。</summary>
    /// <param name="settings">从磁盘读出的设置。</param>
    /// <remarks>
    /// 旧版明文哈希（无保护前缀）原样放行，由下次 <see cref="SaveAsync"/> 自动升级；
    /// 未注入保护器（测试或显式关闭）时原样放行。
    /// </remarks>
    private AppSettings UnprotectHash(AppSettings settings)
    {
        var hash = settings.WebPasswordHash;

        if (_hashProtector is null
            || string.IsNullOrEmpty(hash)
            || !_hashProtector.IsProtected(hash))
        {
            return settings;
        }

        try
        {
            return settings with { WebPasswordHash = _hashProtector.Unprotect(hash) };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // 解密失败说明设置文件被篡改或跨机器复制（DPAPI 绑定用户与机器）：
            // 保留无效哈希只会让共享永远无法登录，置空等价于重置密码。
            AppLog.Warn("Settings", "Web 密码哈希解除保护失败，已重置（需重新设置密码）。");
            return settings with { WebPasswordHash = null };
        }
    }

    /// <summary>保护 Web 密码哈希后返回待持久化的副本；内存中的设置保持明文。</summary>
    /// <param name="settings">归一化后的设置。</param>
    /// <remarks>保护失败（如 DPAPI 不可用）按明文写入并告警——可用性优先于加密。</remarks>
    private AppSettings ProtectHash(AppSettings settings)
    {
        var hash = settings.WebPasswordHash;

        if (_hashProtector is null
            || string.IsNullOrEmpty(hash)
            || _hashProtector.IsProtected(hash))
        {
            return settings;
        }

        try
        {
            return settings with { WebPasswordHash = _hashProtector.Protect(hash) };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLog.Warn("Settings", "Web 密码哈希保护失败，本次按明文写入。");
            return settings;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(ProtectHash(normalized), SerializerOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var temporaryPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
                .ConfigureAwait(false);

            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _current = normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>修正越界的设置值，避免损坏的设置文件把界面带进非法状态。</summary>
    private static AppSettings Normalize(AppSettings settings)
    {
        var viewMode = Enum.IsDefined(settings.ViewMode)
            ? settings.ViewMode
            : GalleryViewMode.Justified;

        var thumbnailSize = ThumbnailSizes.Presets.Contains(settings.ThumbnailSize)
            ? settings.ThumbnailSize
            : ThumbnailSizes.Default;

        var interval = Math.Clamp(settings.SlideShowIntervalSeconds, 1, 3600);

        var transition = Enum.IsDefined(settings.SlideShowTransition)
            ? settings.SlideShowTransition
            : SlideShowTransitionMode.Slide;

        var order = Enum.IsDefined(settings.SlideShowOrder)
            ? settings.SlideShowOrder
            : SlideShowPlayOrder.List;

        var wheelMode = Enum.IsDefined(settings.ViewerWheelMode)
            ? settings.ViewerWheelMode
            : ViewerWheelMode.Zoom;

        var initialZoom = Enum.IsDefined(settings.ViewerInitialZoom)
            ? settings.ViewerInitialZoom
            : ViewerInitialZoom.FitToWindow;

        var musicPaths = NormalizeMusicPaths(settings.MusicLibraryPaths);

        return settings with
        {
            ViewMode = viewMode,
            ThumbnailSize = thumbnailSize,
            SlideShowIntervalSeconds = interval,
            SlideShowOrder = order,
            SlideShowTransition = transition,
            ViewerWheelMode = wheelMode,
            ViewerInitialZoom = initialZoom,
            MusicLibraryPaths = musicPaths
        };
    }

    /// <summary>规范化音乐库目录：丢弃空白与非法项、转绝对路径、去掉结尾分隔符并按大小写无关去重。</summary>
    /// <param name="paths">原始目录集合；为 null 或空时返回空集合。</param>
    /// <returns>规范化后的目录集合。</returns>
    /// <remarks>
    /// 个别坏目录只丢弃自身，不整体失败：设置文件里一个失效路径不应让全部偏好降级为默认值。
    /// 结尾分隔符在此去掉而非保留——与 PathGuard.NormalizeDirectory 的「根目录带分隔符」约定不同，
    /// 这些路径只用于展示与递归枚举，保留分隔符会让界面显示与用户选择的形态不一致。
    /// </remarks>
    private static List<string> NormalizeMusicPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(paths.Count);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
            {
                continue;
            }

            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }
}
