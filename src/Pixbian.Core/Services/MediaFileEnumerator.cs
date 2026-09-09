/**
 * 媒体文件枚举器及其不可访问目录计数器。
 * 职责：递归枚举扫描源下的受支持媒体文件，并统计枚举过程中无法访问的目录数量。
 * 复用约定：由 MediaIndexingService 通过构造注入消费，默认实现走真实文件系统，测试可注入桩实现；
 *          属性过滤复用 System.IO.EnumerationOptions 的 AttributesToSkip，与改造前的递归枚举保持同一语义。
 * 关键约束：IgnoreInaccessible 必须为 false——开启时不可访问目录会被框架静默吞掉，计数恒为 0，
 *          对账便无从判断「本次发现集合是否可信」，误删防护随之失效；
 *          未捕获的枚举异常会让整个扫描失败，故四类可恢复异常一律就地计数并跳过该目录；
 *          遍历用显式栈而非递归，避免深层目录树把栈打满。
 */

using System.IO;

namespace Pixbian.Core.Services;

/// <summary>枚举过程中遇到的不可访问目录计数；跨 yield 边界共享同一实例，由调用方在枚举结束后读取。</summary>
public sealed class InaccessibleDirectoryCounter
{
    private int _count;

    /// <summary>已记录的不可访问目录数量。</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>记录一个不可访问目录。</summary>
    public void Record() => Interlocked.Increment(ref _count);
}

/// <summary>媒体文件枚举器抽象。</summary>
public interface IMediaFileEnumerator
{
    /// <summary>递归枚举指定根目录下的受支持媒体文件。</summary>
    /// <param name="rootDirectory">已规范化的根目录绝对路径。</param>
    /// <param name="inaccessibleDirectories">不可访问目录计数器；枚举过程中就地累加。</param>
    /// <returns>惰性产出的受支持媒体文件。</returns>
    IEnumerable<FileInfo> Enumerate(
        string rootDirectory,
        InaccessibleDirectoryCounter inaccessibleDirectories);
}

/// <summary>基于真实文件系统的媒体文件枚举器。</summary>
public sealed class FileSystemMediaFileEnumerator : IMediaFileEnumerator
{
    /// <summary>共享实例：本类无状态，无需每次扫描新建。</summary>
    public static FileSystemMediaFileEnumerator Instance { get; } = new();

    private static readonly EnumerationOptions SingleLevelOptions = new()
    {
        RecurseSubdirectories = false,

        // 必须显式关闭：开启时框架会吞掉不可访问目录，计数恒为 0，对账的误删防护形同虚设。
        // 异常改由本类捕获并计数，语义等价且可观测。
        IgnoreInaccessible = false,

        // 与改造前一致：跳过重解析点（防目录环与越权读取）与隐藏、系统、临时文件。
        AttributesToSkip = FileAttributes.Hidden
            | FileAttributes.System
            | FileAttributes.Temporary
            | FileAttributes.ReparsePoint
    };

    /// <inheritdoc />
    public IEnumerable<FileInfo> Enumerate(
        string rootDirectory,
        InaccessibleDirectoryCounter inaccessibleDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(inaccessibleDirectories);

        var root = new DirectoryInfo(rootDirectory);

        if (!root.Exists)
        {
            yield break;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            using var entries = directory
                .EnumerateFileSystemInfos("*", SingleLevelOptions)
                .GetEnumerator();

            while (true)
            {
                FileSystemInfo entry;

                try
                {
                    if (!entries.MoveNext())
                    {
                        break;
                    }

                    entry = entries.Current;
                }
                catch (Exception ex) when (IsInaccessible(ex))
                {
                    // 目录不可访问：其子树全部缺席本次发现集合，必须计数以便上层放弃对账。
                    inaccessibleDirectories.Record();
                    break;
                }

                switch (entry)
                {
                    case DirectoryInfo subdirectory:
                        pending.Push(subdirectory);
                        break;

                    case FileInfo file when MediaFileClassifier.IsSupported(file.Name):
                        yield return file;
                        break;
                }
            }
        }
    }

    /// <summary>判定异常是否属于「目录不可访问」这一类可恢复失败。</summary>
    /// <param name="exception">枚举过程中抛出的异常。</param>
    private static bool IsInaccessible(Exception exception) =>
        exception is UnauthorizedAccessException
            or DirectoryNotFoundException
            or FileNotFoundException
            or PathTooLongException
            or IOException;
}
