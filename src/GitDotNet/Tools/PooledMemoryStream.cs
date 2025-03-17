using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace GitDotNet.Tools;

/// <summary>A memory stream that uses a pooled buffer to minimize allocations.</summary>
[ExcludeFromCodeCoverage]
public class PooledMemoryStream(int initialCapacity = 4096) : Stream
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    private int _position = 0;
    private int _length = 0;
    private bool _disposed;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = (int)value;
        }
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        int bytesToRead = Math.Min(count, _length - _position);
        if (bytesToRead <= 0)
            return 0;

        Array.Copy(_buffer, _position, buffer, offset, bytesToRead);
        _position += bytesToRead;
        return bytesToRead;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        int newPosition = origin switch
        {
            SeekOrigin.Begin => (int)offset,
            SeekOrigin.Current => _position + (int)offset,
            SeekOrigin.End => _length + (int)offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0 || newPosition > _length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        _position = newPosition;
        return _position;
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        if (value < 0 || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        EnsureCapacity((int)value);
        _length = (int)value;
        if (_position > _length)
            _position = _length;
    }

    /// <summary>Writes data from the specified stream into the current stream.</summary>
    /// <param name="stream">The stream to read data from.</param>
    /// <param name="count">The number of bytes to write.</param>
    public void Write(Stream stream, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        EnsureCapacity(_position + count);
        var remaining = count;
        while (remaining > 0)
        {
            var read = stream.Read(_buffer, _position, remaining);
            if (read == 0) break;
            remaining -= read;
            _position += read;
        }
        if (_position > _length)
            _length = _position;
    }

    /// <summary>Asynchronously writes data from the specified stream into the current stream.</summary>
    /// <param name="stream">The stream to read data from.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    public async Task WriteAsync(Stream stream, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        EnsureCapacity(_position + count);
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(_buffer.AsMemory(_position, remaining));
            if (read == 0) break;
            remaining -= read;
            _position += read;
        }
        if (_position > _length)
            _length = _position;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        EnsureCapacity(_position + count);
        Array.Copy(buffer, offset, _buffer, _position, count);
        _position += count;
        if (_position > _length)
            _length = _position;
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        EnsureCapacity(_position + 1);
        _buffer[_position++] = value;
        if (_position > _length)
            _length = _position;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    /// <summary>Ensures that the buffer has the specified capacity.</summary>
    /// <param name="capacity">The required capacity.</param>
    public void EnsureCapacity(int capacity)
    {
        if (capacity > _buffer.Length)
        {
            int newCapacity = Math.Max(_buffer.Length * 2, capacity);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
            Array.Copy(_buffer, 0, newBuffer, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = newBuffer;
        }
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PooledMemoryStream));

        if (_position >= _length)
            return -1;

        return _buffer[_position++];
    }

    /// <summary>Gets a read-only span of the bytes in the stream.</summary>
    /// <returns>A read-only span of the bytes in the stream.</returns>
    public ReadOnlySpan<byte> GetByteSpan() => _buffer.AsSpan(0, _length);
}
