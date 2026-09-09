/**
 * Web 服务层的安全边界测试（M7）。
 * 职责：验证鉴权（哈希/会话/失败锁定/限流）、Range 解析与 HTTP 请求解析的防御行为。
 * 复用约定：纯逻辑测试，不启动真实端口；Range 与解析器直接喂畸形输入。
 * 关键约束：安全用例不得删减——哈希格式与随机盐、篡改哈希安全失败、失败锁定、
 *          限流窗口、超大头部拒绝、非法 Range 拒绝，每一条都对应安全自检表的一个检查项。
 *          待补：恒定时间比较与「合法 Base64 但盐长度错误」两条目前无用例，
 *          现有篡改用例走的是 Base64 解析失败分支，未命中长度校验分支。
 */

using System.Text;
using Pixbian.WebServer.Http;
using Pixbian.WebServer.Security;
using Xunit;

namespace Pixbian.WebServer.Tests;

/// <summary>AuthService 测试。</summary>
public sealed class AuthServiceTests
{
    [Fact]
    public void HashPassword_同一密码_每次产生不同盐与哈希()
    {
        var first = AuthService.HashPassword("secret");
        var second = AuthService.HashPassword("secret");

        Assert.NotEqual(first, second);
        Assert.Equal(3, first.Split('.').Length);
    }

    [Fact]
    public void TryLogin_正确密码_签发令牌()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));

        var token = service.TryLogin("secret", "192.168.1.10");

        Assert.NotNull(token);
        Assert.True(service.IsAuthorized(token));
    }

    [Fact]
    public void TryLogin_错误密码_返回空且不授权()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));

        var token = service.TryLogin("wrong", "192.168.1.10");

        Assert.Null(token);
        Assert.False(service.IsAuthorized("anything"));
    }

    [Fact]
    public void TryLogin_被篡改的哈希存储_校验失败而不抛异常()
    {
        // 盐或哈希部分被篡改后必须安全失败，不得崩溃或放行。
        var service = new AuthService("100000.!!!.!!!");

        Assert.Null(service.TryLogin("anything", "192.168.1.10"));
    }

    [Fact]
    public void TryLogin_连续失败五次_锁定该IP()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));

        for (var i = 0; i < 5; i++)
        {
            service.TryLogin("wrong", "10.0.0.1");
        }

        // 即使密码正确，锁定期内也拒绝。
        Assert.Null(service.TryLogin("secret", "10.0.0.1"));
    }

    [Fact]
    public void TryLogin_不同IP互不影响锁定()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));

        for (var i = 0; i < 5; i++)
        {
            service.TryLogin("wrong", "10.0.0.1");
        }

        Assert.Null(service.TryLogin("secret", "10.0.0.1"));
        Assert.NotNull(service.TryLogin("secret", "10.0.0.2"));
    }

    [Fact]
    public void IsAuthorized_未启用密码_全部放行()
    {
        var service = new AuthService(null);

        Assert.True(service.IsAuthorized(null));
        Assert.True(service.IsAuthorized(""));
    }

    [Fact]
    public void IsRateLimited_超出阈值_触发限流()
    {
        var service = new AuthService(null);

        for (var i = 0; i < 300; i++)
        {
            Assert.False(service.IsRateLimited("10.0.0.5"));
        }

        Assert.True(service.IsRateLimited("10.0.0.5"));
        Assert.False(service.IsRateLimited("10.0.0.6"));
    }

    [Fact]
    public void IsRateLimited_登录与一般请求_分桶独立计数()
    {
        var service = new AuthService(null);

        // 登录桶阈值 10：第 11 次超限。
        for (var i = 0; i < 10; i++)
        {
            Assert.False(service.IsRateLimited("10.0.0.5", AuthService.RequestTier.Login));
        }

        Assert.True(service.IsRateLimited("10.0.0.5", AuthService.RequestTier.Login));

        // 一般桶不受登录桶影响：登录刷爆不应拖累正常浏览。
        for (var i = 0; i < 50; i++)
        {
            Assert.False(service.IsRateLimited("10.0.0.5", AuthService.RequestTier.General));
        }
    }

    [Fact]
    public void Revoke_后令牌立即失效()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));
        var token = service.TryLogin("secret", "192.168.1.10");

        Assert.True(service.IsAuthorized(token));

        service.Revoke(token);

        Assert.False(service.IsAuthorized(token));
    }

    [Fact]
    public void GetActiveSessions_登录后返回一条且IP已脱敏()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));
        service.TryLogin("secret", "192.168.1.10");

        var sessions = service.GetActiveSessions();

        var session = Assert.Single(sessions);
        Assert.Equal("192.168.1.0/24", session.MaskedIp);
        Assert.DoesNotContain("192.168.1.10", session.MaskedIp, StringComparison.Ordinal);
    }

    [Fact]
    public void GetActiveSessions_Revoke后_列表清空()
    {
        var service = new AuthService(AuthService.HashPassword("secret"));
        var token = service.TryLogin("secret", "192.168.1.10");

        service.Revoke(token);

        Assert.Empty(service.GetActiveSessions());
    }

    [Fact]
    public void ValidatePasswordStrength_合格密码_通过()
    {
        var result = AuthService.ValidatePasswordStrength("Sunny-Lane-42");

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePasswordStrength_空白_拒绝(string? password)
    {
        Assert.False(AuthService.ValidatePasswordStrength(password).IsValid);
    }

    [Theory]
    [InlineData("a1b2c3d")]
    [InlineData("1234567")]
    public void ValidatePasswordStrength_长度不足8_拒绝(string password)
    {
        Assert.False(AuthService.ValidatePasswordStrength(password).IsValid);
    }

    [Fact]
    public void ValidatePasswordStrength_纯数字_拒绝()
    {
        Assert.False(AuthService.ValidatePasswordStrength("1234567890").IsValid);
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("Pixbian2024")]
    public void ValidatePasswordStrength_弱口令黑名单_拒绝(string password)
    {
        Assert.False(AuthService.ValidatePasswordStrength(password).IsValid);
    }
}

/// <summary>RangeParser 测试。</summary>
public sealed class RangeParserTests
{
    [Fact]
    public void TryParse_标准区间_解析正确()
    {
        Assert.True(RangeParser.TryParse("bytes=0-99", 1000, out var start, out var end));
        Assert.Equal(0, start);
        Assert.Equal(99, end);
    }

    [Fact]
    public void TryParse_开区间到结尾_取到末尾()
    {
        Assert.True(RangeParser.TryParse("bytes=500-", 1000, out var start, out var end));
        Assert.Equal(500, start);
        Assert.Equal(999, end);
    }

    [Fact]
    public void TryParse_后缀区间_取末尾N字节()
    {
        Assert.True(RangeParser.TryParse("bytes=-200", 1000, out var start, out var end));
        Assert.Equal(800, start);
        Assert.Equal(999, end);
    }

    [Fact]
    public void TryParse_end越界_钳制到末尾()
    {
        Assert.True(RangeParser.TryParse("bytes=900-99999", 1000, out var start, out var end));
        Assert.Equal(900, start);
        Assert.Equal(999, end);
    }

    [Theory]
    [InlineData("bytes=500-100", 1000)]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=", 1000)]
    [InlineData("bytes=a-b", 1000)]
    [InlineData("bytes=0-10,20-30", 1000)]
    [InlineData("application/json", 1000)]
    public void TryParse_非法区间_返回false(string header, long length)
    {
        Assert.False(RangeParser.TryParse(header, length, out _, out _));
    }
}

/// <summary>HttpRequestParser 测试。</summary>
public sealed class HttpRequestParserTests
{
    [Fact]
    public void Parse_标准GET请求_正确解析()
    {
        var request = Encoding.UTF8.GetBytes(
            "GET /api/items?kind=image&limit=10 HTTP/1.1\r\nHost: x\r\n\r\n");

        var parsed = HttpRequestParser.Parse(request, request.Length);

        Assert.NotNull(parsed);
        Assert.Equal(HttpMethodKind.Get, parsed.Method);
        Assert.Equal("/api/items", parsed.Path);
        Assert.Equal("image", parsed.QueryValue("kind"));
        Assert.Equal("10", parsed.QueryValue("limit"));
    }

    [Fact]
    public void Parse_带请求体_正确读取()
    {
        var body = Encoding.UTF8.GetBytes("""{"password":"secret"}""");
        var head = Encoding.UTF8.GetBytes(
            $"POST /api/login HTTP/1.1\r\nContent-Length: {body.Length}\r\n\r\n");

        var request = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, request, 0, head.Length);
        Buffer.BlockCopy(body, 0, request, head.Length, body.Length);

        var parsed = HttpRequestParser.Parse(request, request.Length);

        Assert.NotNull(parsed);
        Assert.Equal(HttpMethodKind.Post, parsed.Method);
        Assert.Equal(body, parsed.Body);
    }

    [Fact]
    public void Parse_数据不完整_返回null等待更多数据()
    {
        var partial = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n");

        Assert.Null(HttpRequestParser.Parse(partial, partial.Length));
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nBadHeader\r\n\r\n")]
    public void Parse_畸形请求_抛出BadRequest(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);

        Assert.Throws<BadRequestException>(
            () => HttpRequestParser.Parse(bytes, bytes.Length));
    }

    [Fact]
    public void Parse_绝对URI形式的请求_被拒绝()
    {
        var bytes = Encoding.UTF8.GetBytes("GET http://evil.com/ HTTP/1.1\r\n\r\n");

        Assert.Throws<BadRequestException>(
            () => HttpRequestParser.Parse(bytes, bytes.Length));
    }

    [Fact]
    public void Parse_超大头部_被拒绝()
    {
        var longHeader = new string('a', 20_000);
        var bytes = Encoding.UTF8.GetBytes($"GET / HTTP/1.1\r\nX-Big: {longHeader}\r\n\r\n");

        Assert.Throws<BadRequestException>(
            () => HttpRequestParser.Parse(bytes, bytes.Length));
    }
}
