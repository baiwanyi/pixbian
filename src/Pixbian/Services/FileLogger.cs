/**
 * 把 Microsoft.Extensions.Logging 的日志桥接到应用日志文件的记录器。
 * 职责：让只依赖日志抽象的基础设施（如局域网 Web 服务）把事件统一落到 AppLog。
 * 复用约定：基础设施层只依赖 ILogger 抽象，具体实现由界面层在组合根装配；
 *          类别名只取最后一段，避免每行日志都被完整命名空间占满。
 * 关键约束：正文脱敏由调用侧负责（AppLog.RedactIp / RedactPath），本类不改写消息内容；
 *          BeginScope 无实际作用域支持，返回 null——调用方不得依赖作用域传递字段；
 *          低于 Information 的级别直接丢弃，避免调试级输出在用户机器上写满磁盘。
 */

using Microsoft.Extensions.Logging;
using Pixbian.Core.Utilities;

namespace Pixbian.Services;

/// <summary>写入应用日志文件的日志记录器。</summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;

    /// <summary>初始化记录器。</summary>
    /// <param name="categoryName">日志类别，通常传调用方类型名。</param>
    public FileLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);

        _category = categoryName.Split('.')[^1];
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);

        switch (logLevel)
        {
            case LogLevel.Warning:
                AppLog.Warn(_category, message);
                break;

            case LogLevel.Error:
            case LogLevel.Critical:
                AppLog.Error(_category, message, exception);
                break;

            default:
                AppLog.Info(_category, message);
                break;
        }
    }
}
