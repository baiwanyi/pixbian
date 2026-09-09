/**
 * 限长只读流。
 * 职责：在既有流上截取指定长度的窗口，供 HTTP Range 响应按块输出而不把整段读入内存。
 * 复用约定：只实现读取路径，写入与寻址一律不支持；包装的文件流默认随本流释放。
 * 关键约束：剩余长度必须逐次递减，Read 到上限即返回 0（语义等同流结束），
 *          否则 CopyToAsync 会越过 Range 末端把余下文件一并写进响应体；
 *          媒体文件可达数 GB，任何「按请求范围整段读进 byte[]」的写法都会 OOM。
 */

using System.IO;

namespace Pixbian.WebServer.Http;

/// <summary>在既有流上截取固定长度的只读流；不可寻址、不可写入。</summary>
public sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _remaining;

    /// <summary>初始化限长流。</summary>
    /// <param name="inner">底层流；调用前应已定位到起始位置。</param>
    /// <param name="length">允许读出的字节数。</param>
    /// <param name="leaveOpen">为 true 时不随本流释放底层流。</param>
    public BoundedStream(Stream inner, long length, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _inner = inner;
        _remaining = length;
        _leaveOpen = leaveOpen;
        Length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length { get; }

    /// <inheritdoc />
    public override long Position
    {
        get => Length - _remaining;
        set => throw new NotSupportedException("限长流不支持寻址。");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        if (_remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);

        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await _inner
            .ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken)
            .ConfigureAwait(false);

        _remaining -= read;
        return read;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("限长流不支持寻址。");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("限长流不可写入。");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("限长流不可写入。");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
