using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;

namespace GitDotNet.Tools;

/// <summary>
/// A stream wrapper that computes CRC32 while writing data to the underlying stream.
/// This class optimizes CRC32 calculation by computing it incrementally as data flows through,
/// eliminating the need for temporary memory buffers and reducing memory allocation overhead.
/// </summary>
[ExcludeFromCodeCoverage]
internal class Crc32TrackingStream(Stream underlyingStream) : Stream
{
    private readonly Crc32 _crc32 = new();
    private bool _disposed;

    public override bool CanRead => false;

    public override bool CanSeek => underlyingStream.CanSeek;

    public override bool CanWrite => !_disposed && underlyingStream.CanWrite;

    public override long Length => underlyingStream.Length;

    public override long Position
    {
        get => underlyingStream.Position;
        set => underlyingStream.Position = value;
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Crc32TrackingStream));
        underlyingStream.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Crc32TrackingStream));
        await underlyingStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Crc32TrackingStream does not support reading.");

    public override long Seek(long offset, SeekOrigin origin) =>
        underlyingStream.Seek(offset, origin);

    public override void SetLength(long value) =>
        underlyingStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Crc32TrackingStream));
        ArgumentNullException.ThrowIfNull(buffer);
        
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");
        if (offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "The sum of offset and count exceeds the buffer length.");

        // Update CRC32 with the data being written
        _crc32.Append(buffer.AsSpan(offset, count));
        
        // Write to underlying stream
        underlyingStream.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Crc32TrackingStream));
        ArgumentNullException.ThrowIfNull(buffer);
        
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");
        if (offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "The sum of offset and count exceeds the buffer length.");

        // Update CRC32 with the data being written
        _crc32.Append(buffer.AsSpan(offset, count));
        
        // Write to underlying stream
        await underlyingStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Crc32TrackingStream));

        // Update CRC32 with the data being written
        _crc32.Append(buffer.Span);
        
        // Write to underlying stream
        await underlyingStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void WriteByte(byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Crc32TrackingStream));

        // Update CRC32 with the byte being written
        _crc32.Append(new ReadOnlySpan<byte>([value]));
        
        // Write to underlying stream
        underlyingStream.WriteByte(value);
    }

    /// <summary>Gets the computed CRC32 hash of all data written to this stream.</summary>
    /// <returns>The CRC32 hash as a byte array.</returns>
    public byte[] GetCrc32Hash()
    {
        return _crc32.GetHashAndReset();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Don't dispose the underlying stream as it's not owned by this wrapper
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            // Don't dispose the underlying stream as it's not owned by this wrapper
            _disposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}