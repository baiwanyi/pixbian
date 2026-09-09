/**
 * 回收站辅助：将文件或文件夹移入系统回收站而非永久删除。
 * 职责：封装 Microsoft.VisualBasic.FileIO.FileSystem 的回收站删除——
 *      这是官方对已弃用 SHFileOperation 的继任路径，内部经 Shell API 执行并正确处理长路径；
 *      同时提供 IRecycleBinService 抽象供视图模型注入（单元测试以桩替换，不触碰真实回收站）。
 * 复用约定：纯 .NET API（共享框架自带 Microsoft.VisualBasic），无额外依赖；
 *          调用方在 UI 线程同步调用即可；注入方经 DI 注册 RecycleBinService。
 * 关键约束：用户在错误对话框中取消与各类失败一律返回 false，由调用方提示；
 *          本方法不触碰数据库索引，索引清理由调用方（GalleryViewModel）负责。
 */

using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace Pixbian.Services;

/// <summary>回收站操作抽象：视图模型依赖本接口而非静态类，保证删除链路可离线测试。</summary>
public interface IRecycleBinService
{
    /// <summary>将指定文件或文件夹（含全部内容）移入回收站。</summary>
    /// <param name="path">文件或目录的完整路径。</param>
    /// <returns>成功移入回收站返回 true；失败（不存在、被占用、权限不足或用户取消）返回 false。</returns>
    bool SendToRecycleBin(string path);
}

/// <summary>回收站服务的默认实现：转发到共享静态辅助。</summary>
public sealed class RecycleBinService : IRecycleBinService
{
    /// <inheritdoc />
    public bool SendToRecycleBin(string path) => RecycleBinHelper.SendToRecycleBin(path);
}

/// <summary>回收站操作辅助：把文件移入回收站。</summary>
public static class RecycleBinHelper
{
    /// <summary>将指定文件或文件夹（含全部内容）移入回收站。</summary>
    /// <param name="path">文件或目录的完整路径。</param>
    /// <returns>成功移入回收站返回 true；失败（不存在、被占用、权限不足或用户取消）返回 false。</returns>
    public static bool SendToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }

            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            // 用户在 shell 错误对话框中选择取消，或删除被占用/无权限：按失败返回。
            return false;
        }
    }
}
