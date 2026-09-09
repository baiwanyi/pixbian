/**
 * Web 访问的鉴权与限流服务（M7）。
 * 职责：密码哈希校验、会话令牌管理、按 IP 的请求限流与登录失败锁定。
 * 复用约定：密码用 PBKDF2（Rfc2898DeriveBytes，SHA-256，10 万次迭代，16 字节随机盐）；
 *          会话令牌为 256 位 CSPRNG 随机值，只存内存，重启即失效；限流为固定窗口计数。
 * 关键约束：哈希存储格式为「迭代数(十进制).盐(Base64).哈希(Base64)」，盐与哈希长度必须校验，
 *          防止篡改后的存储值绕过长度检查；
 *          恒定时间比较（CryptographicOperations.FixedTimeEquals）**只用于密码哈希**，
 *          会话令牌仅在服务端字典中按键查找，不参与客户端可控的比较路径；
 *          登录失败锁定与请求限流的键为套接字来源 IP，不可被客户端伪造。
 */

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pixbian.Core.Utilities;

namespace Pixbian.WebServer.Security;

/// <summary>对外暴露的活跃会话信息（IP 已脱敏）。</summary>
/// <param name="MaskedIp">脱敏后的来源 IP。</param>
/// <param name="CreatedUtc">签发时间（UTC）。</param>
/// <param name="ExpiresUtc">过期时间（UTC）。</param>
public sealed record ActiveSession(string MaskedIp, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc);

/// <summary>鉴权与限流服务。</summary>
public sealed partial class AuthService
{
    // 审计事件：登录成败与锁定是安全事件的主要证据，缺失时暴力破解与滥用无法追溯。
    // IP 一律经 AppLog.RedactIp 脱敏，日志可能随用户反馈外发。
    [LoggerMessage(EventId = 7101, Level = LogLevel.Information,
        Message = "登录成功：{RemoteIp}")]
    private static partial void LogLoginSucceeded(ILogger logger, string remoteIp);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning,
        Message = "登录失败（密码错误）：{RemoteIp}，连续失败 {Attempts} 次")]
    private static partial void LogLoginFailed(ILogger logger, string remoteIp, int attempts);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning,
        Message = "登录失败次数达上限，已锁定来源：{RemoteIp}")]
    private static partial void LogLoginLocked(ILogger logger, string remoteIp);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Warning,
        Message = "来源已被锁定，拒绝登录尝试：{RemoteIp}")]
    private static partial void LogLoginRejectedWhileLocked(ILogger logger, string remoteIp);

    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 100_000;
    private const int TokenBytes = 32;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    /// <summary>一般请求（静态资源、列表、媒体）的限流阈值。</summary>
    private const int GeneralRequestsPerMinute = 300;

    /// <summary>登录请求的限流阈值：PBKDF2 校验成本高，且登录是暴力破解的直接入口，须远紧于一般请求。</summary>
    private const int LoginRequestsPerMinute = 10;

    private readonly string? _passwordHash;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lockedExceptions = new();
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _requestCounts = new();
    private readonly ConcurrentDictionary<string, (int Attempts, DateTimeOffset LockedUntil)> _failedLogins = new();

    /// <summary>单个活跃会话的元数据。</summary>
    /// <param name="ExpiresUtc">过期时间（UTC）。</param>
    /// <param name="RemoteIp">签发时的来源 IP；仅存内存，读取展示时再脱敏。</param>
    /// <param name="CreatedUtc">签发时间（UTC）。</param>
    private sealed record SessionEntry(DateTimeOffset ExpiresUtc, string RemoteIp, DateTimeOffset CreatedUtc);

    /// <summary>初始化鉴权服务。</summary>
    /// <param name="storedPasswordHash">已存储的密码哈希（HashPassword 的输出格式）；为空表示不启用鉴权。</param>
    /// <param name="logger">日志记录器；为空时使用空实现，此时安全事件不落盘。</param>
    public AuthService(string? storedPasswordHash, ILogger? logger = null)
    {
        _passwordHash = string.IsNullOrWhiteSpace(storedPasswordHash) ? null : storedPasswordHash;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>共享密码的弱口令黑名单：常见连续数字、键盘序与本项目名。</summary>
    private static readonly FrozenSet<string> WeakPasswords = new[]
    {
        "12345678", "123456789", "1234567890", "password", "password1",
        "qwerty123", "11111111", "88888888", "00000000", "abc12345678", "pixbian"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>校验共享密码强度：长度下限 8，且不得为纯数字或已知弱口令。</summary>
    /// <param name="password">待设置的明文密码。</param>
    /// <returns>校验结果；通过时 <see cref="PasswordStrengthResult.ErrorMessage"/> 为空。</returns>
    /// <remarks>
    /// 该密码保护的是整个媒体库的远程只读访问，且传输为明文 HTTP（见 S-01），
    /// 强度是攻击成本的主要来源，故宁严勿松。
    /// </remarks>
    public static PasswordStrengthResult ValidatePasswordStrength(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return new PasswordStrengthResult(false, "密码不能为空。");
        }

        if (password.Length < 8)
        {
            return new PasswordStrengthResult(false, "密码至少需要 8 个字符。");
        }

        if (password.All(char.IsAsciiDigit))
        {
            return new PasswordStrengthResult(false, "密码不能为纯数字。");
        }

        if (WeakPasswords.Any(pattern => password.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            // 包含语义而非精确匹配：密码「Pixbian2024」这类「项目名 + 年份」组合同样应被拒绝。
            return new PasswordStrengthResult(false, "密码包含过于常见的词，请更换。");
        }

        return new PasswordStrengthResult(true, string.Empty);
    }

    /// <summary>密码强度校验结果。</summary>
    /// <param name="IsValid">是否通过。</param>
    /// <param name="ErrorMessage">失败原因；通过时为空。</param>
    public sealed record PasswordStrengthResult(bool IsValid, string ErrorMessage);

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

        var redactedIp = AppLog.RedactIp(remoteIp);

        if (DateTimeOffset.UtcNow < lockedUntil)
        {
            LogLoginRejectedWhileLocked(_logger, redactedIp);
            return null;
        }

        if (string.IsNullOrWhiteSpace(password) || !VerifyPassword(password, _passwordHash))
        {
            attempts++;

            if (attempts >= MaxFailedAttempts)
            {
                // 连续失败达到上限，锁定该 IP 一段时间，防暴力破解。
                _failedLogins[remoteIp] = (0, DateTimeOffset.UtcNow.Add(LockDuration));
                LogLoginLocked(_logger, redactedIp);
            }
            else
            {
                _failedLogins[remoteIp] = (attempts, DateTimeOffset.MinValue);
                LogLoginFailed(_logger, redactedIp, attempts);
            }

            return null;
        }

        _failedLogins.TryRemove(remoteIp, out _);
        LogLoginSucceeded(_logger, redactedIp);

        var token = GenerateToken();
        var now = DateTimeOffset.UtcNow;
        _sessions[token] = new SessionEntry(now.Add(SessionLifetime), remoteIp, now);
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
            && session.ExpiresUtc > DateTimeOffset.UtcNow;
    }

    /// <summary>读取当前全部活跃会话（IP 已脱敏）；供设置页展示，服务端绝不回传原始令牌。</summary>
    public IReadOnlyList<ActiveSession> GetActiveSessions() =>
        [.. _sessions.Values.Select(s => new ActiveSession(
            AppLog.RedactIp(s.RemoteIp), s.CreatedUtc, s.ExpiresUtc))];

    /// <summary>吊销指定会话令牌；令牌为空或不存在时静默（登出幂等）。</summary>
    /// <param name="token">待吊销的令牌。</param>
    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _sessions.TryRemove(token, out _);
        }
    }

    /// <summary>吊销全部会话。</summary>
    public void RevokeAll() => _sessions.Clear();

    /// <summary>请求限流类别：登录与一般请求分别计数，避免彼此挤兑。</summary>
    public enum RequestTier
    {
        /// <summary>静态资源、列表与媒体请求。</summary>
        General = 0,

        /// <summary>登录请求；PBKDF2 校验成本高且是暴力破解入口，阈值远紧于一般请求。</summary>
        Login = 1
    }

    /// <summary>判断请求是否超过所属类别的限流阈值。</summary>
    /// <param name="remoteIp">来源 IP。</param>
    /// <param name="tier">请求类别；各类别独立计数。</param>
    /// <returns>超限时返回 true，调用方应返回 429。</returns>
    public bool IsRateLimited(string remoteIp, RequestTier tier = RequestTier.General)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteIp);

        var now = DateTimeOffset.UtcNow;
        var bucket = $"{remoteIp}:{(int)tier}";
        var (count, windowStart) = _requestCounts.GetOrAdd(bucket, (0, now));

        // 固定窗口：距窗口起点超过 1 分钟则重置计数。
        if (now - windowStart > TimeSpan.FromMinutes(1))
        {
            count = 0;
            windowStart = now;
        }

        count++;
        _requestCounts[bucket] = (count, windowStart);

        var limit = tier == RequestTier.Login ? LoginRequestsPerMinute : GeneralRequestsPerMinute;

        return count > limit;
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

    /// <summary>清理过期会话、过期限流窗口与已过期的登录锁定记录；在登录时机触发以摊薄成本。</summary>
    /// <remarks>
    /// 三张表都只增不减，长时间运行会被扫描型客户端撑大（审计 S-05）；
    /// 登录是低频事件，顺带清理的成本可忽略，不必引入定时器。
    /// </remarks>
    private void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in _sessions.Where(s => s.Value.ExpiresUtc <= now))
        {
            _sessions.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _requestCounts.Where(p => now - p.Value.WindowStart > TimeSpan.FromMinutes(5)))
        {
            _requestCounts.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _failedLogins.Where(p =>
                     p.Value.LockedUntil != DateTimeOffset.MinValue && now >= p.Value.LockedUntil))
        {
            _failedLogins.TryRemove(pair.Key, out _);
        }
    }
}
