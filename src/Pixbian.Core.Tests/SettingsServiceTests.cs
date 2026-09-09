/**
 * 设置服务的加解密协作测试。
 * 职责：验证 Web 密码哈希在「保存加密、加载解密」边界上的行为，以及旧版明文哈希的自动升级
 *      与损坏哈希的安全降级——这些是设置文件作为敏感数据落盘的正确性底线。
 * 复用约定：使用桩保护器（可逆的字符串前缀变换），不依赖 DPAPI（平台专有，测试环境不可控）；
 *          每个用例使用独立的临时设置文件路径，不触碰用户真实配置。
 * 关键约束：必须覆盖三类迁移路径——受保护值正常解密、旧版明文原样加载、损坏值安全降级为空，
 *          其中「降级为空」对应 DPAPI 绑定用户与机器后跨环境失效的真实场景。
 */

using System.Security.Cryptography;
using System.Text;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Xunit;

namespace Pixbian.Core.Tests;

/// <summary>JsonSettingsService 与 IHashProtector 的协作测试。</summary>
public sealed class SettingsServiceTests : IDisposable
{
    /// <summary>测试用明文哈希（PBKDF2 存储格式；内容有效性由 AuthService 自身的测试覆盖）。</summary>
    private const string PlainHash =
        "100000.AAAAAAAAAAAAAAAA==.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";

    private readonly string _settingsPath;

    /// <summary>创建临时设置文件路径（文件本身惰性创建）。</summary>
    public SettingsServiceTests()
    {
        _settingsPath = Path.Combine(
            Path.GetTempPath(), "Pixbian.Tests", $"{Guid.NewGuid():N}.settings.json");
    }

    /// <summary>清理临时设置文件。</summary>
    public void Dispose()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
    }

    [Fact]
    public async Task SaveAsync_启用保护器_落盘为受保护格式且内存保持明文()
    {
        var service = new JsonSettingsService(_settingsPath, new StubProtector());
        var hash = PlainHash;

        await service.SaveAsync(new AppSettings { WebPasswordHash = hash });

        var onDisk = await File.ReadAllTextAsync(_settingsPath);
        Assert.Contains(StubProtector.Prefix, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, onDisk, StringComparison.Ordinal);
        Assert.Equal(hash, service.Current.WebPasswordHash);
    }

    [Fact]
    public async Task LoadAsync_受保护哈希_解密后恢复明文()
    {
        var hash = PlainHash;

        // 先用同一套保护器写出受保护格式，再新开实例读取。
        var writer = new JsonSettingsService(_settingsPath, new StubProtector());
        await writer.SaveAsync(new AppSettings { WebPasswordHash = hash });

        var reader = new JsonSettingsService(_settingsPath, new StubProtector());
        await reader.LoadAsync();

        Assert.Equal(hash, reader.Current.WebPasswordHash);
    }

    [Fact]
    public async Task LoadAsync_旧版明文哈希_原样加载不报错()
    {
        // 旧版本写入的明文 PBKDF2 哈希（形如 迭代数.盐.哈希），不带保护前缀。
        var legacyHash = "100000.AAAAAAAAAAAAAAAA==.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
        await File.WriteAllTextAsync(_settingsPath, $$"""{"webPasswordHash":"{{legacyHash}}"}""");

        var service = new JsonSettingsService(_settingsPath, new StubProtector());
        await service.LoadAsync();

        Assert.Equal(legacyHash, service.Current.WebPasswordHash);
    }

    [Fact]
    public async Task SaveAsync_旧版明文哈希_保存时自动升级为受保护格式()
    {
        var legacyHash = "100000.AAAAAAAAAAAAAAAA==.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
        await File.WriteAllTextAsync(_settingsPath, $$"""{"webPasswordHash":"{{legacyHash}}"}""");

        var service = new JsonSettingsService(_settingsPath, new StubProtector());
        await service.LoadAsync();
        await service.SaveAsync(service.Current);

        var onDisk = await File.ReadAllTextAsync(_settingsPath);
        Assert.Contains(StubProtector.Prefix, onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_受保护哈希损坏_降级为空而非带病运行()
    {
        // 跨机器复制后 DPAPI 解密必然失败：保留无效哈希会让 Web 共享永远无法登录。
        await File.WriteAllTextAsync(
            _settingsPath,
            $$"""{"webPasswordHash":"{{StubProtector.Prefix}}bm90LXZhbGlk"}""");

        var service = new JsonSettingsService(_settingsPath, new ThrowingProtector());
        await service.LoadAsync();

        Assert.Null(service.Current.WebPasswordHash);
    }

    /// <summary>Base64 变换式保护器：受保护值中不含明文子串，与真实加密的可观测行为一致。</summary>
    private sealed class StubProtector : IHashProtector
    {
        public const string Prefix = "enc:";

        public bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

        public string Protect(string plaintext) =>
            Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
    }

    /// <summary>始终解密失败的保护器：模拟 DPAPI 跨机器失效。</summary>
    private sealed class ThrowingProtector : IHashProtector
    {
        public bool IsProtected(string value) => true;

        public string Protect(string plaintext) => throw new CryptographicException();

        public string Unprotect(string protectedValue) => throw new CryptographicException();
    }
}
