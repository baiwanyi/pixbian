/**
 * 回收站辅助：将文件移入系统回收站而非永久删除。
 * 职责：封装 SHFileOperation（shell32.dll）的删除并带 FOF_ALLOWUNDO 标志，确保删除可还原。
 * 复用约定：纯 P/Invoke，无托管依赖；调用方在 UI 线程同步调用即可（SHFileOperation 为同步 API）。
 * 关键约束：pFrom 必须以双 NUL 结尾；删除失败以返回值 0 判定，非异常；
 *          本方法不触碰数据库索引，索引清理由调用方（GalleryViewModel）负责。
 */

using System.IO;
using System.Runtime.InteropServices;

namespace Pixbian.Services;

/// <summary>回收站操作辅助：把文件移入回收站。</summary>
public static class RecycleBinHelper
{
    private const int FoDelete = 0x0003;
    private const ushort FofAllowundo = 0x0040;
    private const ushort FofNoconfirmation = 0x0010;
    private const ushort FofNoerrorui = 0x0400;
    private const ushort FofSilent = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public int wFunc;
        public string? pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>将指定文件移入回收站。</summary>
    /// <param name="path">文件完整路径。</param>
    /// <returns>成功移入回收站返回 true；失败（文件不存在、被占用或权限不足）返回 false。</returns>
    public static bool SendToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            // SHFileOperation 以双 NUL 结尾的字符串数组表示多个路径，单文件也需补第二 NUL。
            pFrom = path + "\0\0",
            fFlags = FofAllowundo | FofNoconfirmation | FofNoerrorui | FofSilent,
        };

        return SHFileOperation(ref op) == 0;
    }
}
