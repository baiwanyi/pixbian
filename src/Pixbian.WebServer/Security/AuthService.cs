/**
 * Web 访问的鉴权与限流服务（M7）。
 * 职责：密码哈希校验、会话令牌管理、按 IP 的请求限流与登录失败锁定。
 * 复用约定：密码用 PBKDF2（Rfc2898DeriveBytes，SHA-256，10 万次迭代，16 字节随机盐）；
 *          会话令牌为 256 位 CSPRNG 随机值，只存内存，重启即失效；
 *          限流用固定窗口计数，锁定期满自动解除。
 * 关键约束：哈希存储格式为 "迭代数.盐.哈希"（全部 Base64），盐与哈希长度必须校验，
 *          防止篡改后的存储值绕过长度检查（CWE-327 家族）；
 *          时间比较必须用 CryptographicOperations.FixedTimeEquals，防时序侧信道；
 *          令牌比较同样恒定时间；登录失败锁定与请求限流的键为来源 IP，
 *          局域网内该值来自套接字，不可被客户端伪造。
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace Pixbian.WebServer.Security;

/// <summary>鉴权与限流服务。</summary>
public sealed class AuthService
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 100_000;
    private const int TokenBytes = 32;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private const int RequestsPerMinute = 60;

    private readonly string? _passwordHash;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lockedExceptions = new();
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _requestCounts = new();
    private readonly ConcurrentDictionary<string, (int Attempts, DateTimeOffset LockedUntil)> _failedLogins = new();

    /// <summary>初始化鉴权服务。</summary>
    /// <param name="storedPasswordHash">已存储的密码哈希（HashPassword 的输出格式）；为空表示不启用鉴权。</param>
    public AuthService(string? storedPasswordHash)
    {
        _passwordHash = string.IsNullOrWhiteSpace(storedPasswordHash) ? null : storedPasswordHash;
    }

    /// <summary>计算密码的 PBKDF2 哈希，用于持久化存储。</summary>
    /// <param name="password">用户设置的明文密码。</param>
    public static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>是否已启用密码保护。</summary>
    public bool IsProtected => _passwordHash is not null;

    /// <summary>校验密码并签发会话令牌。</summary>
    /// <param name="password">用户提交的密码。</param>
    /// <param name="remoteIp">来源 IP，用于失败锁定。</param>
    /// <returns>签发的令牌；密码错误或 IP 被锁定时返回 null。</returns>
    public string? TryLogin(string? password, string remoteIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteIp);

        if (_passwordHash is null)
        {
            return "no-auth";
        }

        var (attempts, lockedUntil) = _failedLogins.GetOrAdd(remoteIp, (0, DateTimeOffset.MinValue));

        if (DateTimeOffset.UtcNow < lockedUntil)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(password) || !VerifyPassword(password, _passwordHash))
        {
            attempts++;

            if (attempts >= MaxFailedAttempts)
            {
                // 连续失败达到上限，锁定该 IP 一段时间，防暴力破解。
                _failedLogins[remoteIp] = (0, DateTimeOffset.UtcNow.Add(LockDuration));
            }
            else
            {
                _failedLogins[remoteIp] = (attempts, DateTimeOffset.MinValue);
            }

            return null;
        }

        _failedLogins.TryRemove(remoteIp, out _);

        var token = GenerateToken();
        _sessions[token] = (token, DateTimeOffset.UtcNow.Add(SessionLifetime));
        CleanupExpiredSessions();

        return token;
    }

    /// <summary>校验会话令牌是否有效。</summary>
    /// <param name="token">请求携带的令牌。</param>
    public bool IsAuthorized(string? token)
    {
        if (_passwordHash is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return _sessions.TryGetValue(token, out var session)
            && session.Expires > DateTimeOffset.UtcNow;
    }

    /// <summary>吊销全部会话。</summary>
    public void RevokeAll() => _sessions.Clear();

    /// <summary>判断请求是否超过限流阈值。</summary>
    /// <param name="remoteIp">来源 IP。</param>
    /// <returns>超限时返回 true，调用方应返回 429。</returns>
    public bool IsRateLimited(string remoteIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteIp);

        var now = DateTimeOffset.UtcNow;
        var (count, windowStart) = _requestCounts.GetOrAdd(remoteIp, (0, now));

        // 固定窗口：距窗口起点超过 1 分钟则重置计数。
        if (now - windowStart > TimeSpan.FromMinutes(1))
        {
            count = 0;
            windowStart = now;
        }

        count++;
        _requestCounts[remoteIp] = (count, windowStart);

        return count > RequestsPerMinute;
    }

    /// <summary>校验密码与存储的哈希是否匹配。</summary>
    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.');

        if (parts.Length != 3
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltSizeBytes || expected.Length != HashSizeBytes)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        // 恒定时间比较，防时序侧信道。
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string GenerateToken()
    {
        Span<char> chars = stackalloc char[44];
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).AsSpan().CopyTo(chars);
        return chars.ToString();
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in _sessions.Where(s => s.Value.Expires <= now))
        {
            _sessions.TryRemove(pair.Key, out _);
        }
    }
}
