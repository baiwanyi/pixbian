/**
 * 音频文件分类器模块。
 * 职责：依据文件扩展名判定音频文件，供音乐库扫描筛选短片页的背景音乐候选。
 * 复用约定：扩展名集合使用大小写不敏感的 FrozenSet，匹配复杂度 O(1) 且无装箱。
 * 关键约束：本分类器只服务音乐库，禁止并入 MediaFileClassifier——后者供图库扫描共用，
 *           一旦支持音频，图库目录下的音频文件会被扫进图库并出现在图库页与计数中。
 */

using System.Collections.Frozen;
using System.IO;

namespace Pixbian.Core.Services;

/// <summary>依据扩展名判定音频文件的分类器。</summary>
public static class AudioFileClassifier
{
    private static readonly FrozenSet<string> AudioExtensions = new[]
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus",
        ".wma", ".aiff", ".aif", ".alac", ".ape", ".mp2", ".amr"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>判断文件是否为受支持的音频文件。</summary>
    /// <param name="fileName">文件名或路径，仅取其中的扩展名部分参与判定。</param>
    /// <returns>命中音频白名单时返回 true，否则返回 false。</returns>
    /// <exception cref="ArgumentException">文件名为空白时抛出。</exception>
    public static bool IsAudio(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extension = Path.GetExtension(fileName);
        return extension.Length != 0 && AudioExtensions.Contains(extension);
    }
}
