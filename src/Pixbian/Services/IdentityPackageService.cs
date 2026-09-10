/**
 * 稀疏包（外部位置包）注册状态服务。
 * 职责：查询本机是否已注册 Pixbian 身份包，并引导用户到系统设置把 Pixbian 设为默认应用。
 * 复用约定：包查询统一走 PackageManager 的按用户枚举并以 Identity.Name 匹配，
 *          避开含发布者哈希、无法预先拼出的包家族名；系统设置跳转走 Launcher.LaunchUriAsync。
 * 关键约束：稀疏包依赖 Windows 10 19041 引入的 uap10:AllowExternalContent，更低版本必须整体跳过；
 *          本服务只读状态，不执行注册与反注册——应用内部署常因权限不足失败，
 *          注册统一由 scripts/Register-Pixbian.ps1 完成，界面只负责提示与引导。
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace Pixbian.Services;

/// <summary>Pixbian 稀疏包的注册状态查询与系统设置引导。</summary>
public static class IdentityPackageService
{
    /// <summary>稀疏包清单中的 Identity.Name，与 app.manifest 的 msix packageName 一致。</summary>
    public const string PackageName = "Pixbian";

    /// <summary>注册脚本的仓库相对路径；「未注册」时提示用户运行它。</summary>
    public const string RegisterScriptPath = @"scripts\Register-Pixbian.ps1";

    /// <summary>支持稀疏包的最低 Windows 内部版本号（Windows 10 2004）。</summary>
    private const int MinSupportedBuild = 19041;

    /// <summary>当前系统是否支持稀疏包；低于 Windows 10 2004 时不支持。</summary>
    public static bool IsSupported =>
        Environment.OSVersion.Version is { Major: >= 10 } version && version.Build >= MinSupportedBuild;

    /// <summary>本机是否已注册 Pixbian 稀疏包。</summary>
    public static bool IsRegistered => FindPackage() is not null;

    /// <summary>查询已注册的 Pixbian 稀疏包；未注册时返回 null。</summary>
    public static Package? FindPackage()
    {
        var manager = new PackageManager();

        return manager
            .FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main)
            .FirstOrDefault(package => string.Equals(package.Id.Name, PackageName, StringComparison.Ordinal));
    }

    /// <summary>获取供界面展示的注册状态说明；系统不支持或未注册时给出对应指引。</summary>
    public static string GetStatusText()
    {
        if (!IsSupported)
        {
            return "当前系统不支持（需 Windows 10 2004 及以上）";
        }

        var package = FindPackage();

        return package is null
            ? $"未注册：还不能设为默认应用，请运行 {RegisterScriptPath}"
            : $"已注册（{FormatVersion(package.Id.Version)}）：可在系统设置中设为默认";
    }

    /// <summary>打开系统「默认应用」设置页，供用户按文件类型把 Pixbian 设为默认。</summary>
    public static async Task OpenDefaultAppsSettingsAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    /// <summary>把包版本格式化为「主.次.构建.修订」。</summary>
    /// <param name="version">包版本结构体。</param>
    private static string FormatVersion(PackageVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
}
