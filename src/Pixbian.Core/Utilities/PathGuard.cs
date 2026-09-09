/**
 * 路径安全守卫模块。
 * 职责：提供目录规范化与目录归属判定，阻断路径穿越（CWE-22）攻击。
 * 复用约定：所有来自外部的路径（局域网请求参数、监控事件、用户配置）在触达文件系统前必须经本模块校验；
 *          判定基于 Path.GetFullPath 规范化后的绝对路径，且统一忽略大小写以适配 Windows 语义。
 * 关键约束：根目录一律规范化为以分隔符结尾，使 /Lib 不会误匹配 /Lib2 这类前缀欺骗路径；
 *          不得用 StartsWith 裸比较相对路径，必须先 GetFullPath 再比对，否则 .. 可被绕过；
 *          候选路径比较前也统一补尾分隔符，使根目录自身按文档语义判定为「内部」；
 *          `\\?\` 长路径前缀与 8.3 短名不被 GetFullPath 展开，比较必然失败——这是拒绝式
 *          安全默认（宁可误拒，不因未展开的别名放行越界路径），行为由测试固化。
 */

using System.IO;

namespace Pixbian.Core.Utilities;

/// <summary>路径安全校验工具。</summary>
public static class PathGuard
{
    /// <summary>将目录路径规范化为绝对路径，并确保以目录分隔符结尾。</summary>
    /// <param name="path">待规范化的目录路径。</param>
    /// <returns>以分隔符结尾的绝对目录路径。</returns>
    /// <exception cref="ArgumentException">路径为空白时抛出。</exception>
    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    /// <summary>判断给定路径是否位于指定根目录之内（含根目录自身）。</summary>
    /// <param name="rootDirectory">根目录。</param>
    /// <param name="candidatePath">待判定路径，可为相对路径。</param>
    /// <returns>位于根目录内时返回 true，否则返回 false。</returns>
    /// <remarks>
    /// 候选路径规范化后统一补尾分隔符再比较：裸的根目录自身因此判定为内部，
    /// 同时不影响文件路径的归属判定（文件路径补分隔符后仍以根前缀开头）。
    /// </remarks>
    public static bool IsInside(string rootDirectory, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = NormalizeDirectory(rootDirectory);
        var candidate = NormalizeForCompare(candidatePath);

        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>尝试将候选路径解析为绝对路径，并校验其位于根目录之内。</summary>
    /// <param name="rootDirectory">根目录。</param>
    /// <param name="candidatePath">候选路径，可为相对路径。</param>
    /// <param name="fullPath">校验通过时的绝对路径；失败时为 <see cref="string.Empty"/>。</param>
    /// <returns>解析成功且在根目录内时返回 true，否则返回 false。</returns>
    public static bool TryResolveInside(
        string rootDirectory,
        string candidatePath,
        out string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = NormalizeDirectory(rootDirectory);
        var resolved = Path.GetFullPath(candidatePath);

        if (!NormalizeForCompare(candidatePath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = resolved;
        return true;
    }

    /// <summary>规范化候选路径用于前缀比较：GetFullPath 后补尾分隔符，使根目录自身判定为内部。</summary>
    private static string NormalizeForCompare(string candidatePath)
    {
        var fullPath = Path.GetFullPath(candidatePath);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
