/**
 * 基于 DPAPI（CurrentUser 作用域）的设置敏感字段保护器。
 * 职责：把 Web 密码哈希以「绑定当前用户与机器」的方式加密后落盘，防设置文件被复制到
 *      其他机器或被其他用户读取后离线破解。
 * 复用约定：实现 Core.Abstractions 的 IHashProtector，由组合根注入 JsonSettingsService；
 *          DPAPI 是 Windows 专有能力，故本类只存在于界面层，领域层经抽象消费。
 * 关键约束：受保护值必须带固定前缀，既是格式标记也是格式版本（迁移时按前缀识别）；
 *          CurrentUser 作用域下同机同用户的任意进程仍可解密，本保护针对的是
 *          「文件被复制走」与「其他用户账户读取」两类场景，不能替代进程隔离。
 */

using System.Security.Cryptography;
using System.Text;
using Pixbian.Core.Abstractions;

namespace Pixbian.Services;

/// <summary>DPAPI（CurrentUser）敏感字段保护器。</summary>
public sealed class DpapiHashProtector : IHashProtector
{
    /// <summary>受保护值前缀：格式标记 + 版本号，未来更换加密方案时按前缀迁移。</summary>
    private const string Prefix = "dpapi:v1:";

    /// <summary>附加熵：提高与其他程序保护数据的区分度（对能读取本程序集的攻击者不构成屏障）。</summary>
    private static readonly byte[] Entropy = "Pixbian.WebPasswordHash.v1"u8.ToArray();

    /// <inheritdoc />
    public bool IsProtected(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            Entropy,
            DataProtectionScope.CurrentUser);

        return Prefix + Convert.ToBase64String(encrypted);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);

        if (!IsProtected(protectedValue))
        {
            throw new CryptographicException("输入不是受保护格式。");
        }

        var decrypted = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue[Prefix.Length..]),
            Entropy,
            DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(decrypted);
    }
}
