using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;

namespace GitDotNet.Readers;

[ExcludeFromCodeCoverage]
internal class SlidingMemoryMappedStream : Stream
{
    private readonly MemoryMappedFile _memoryMappedFile;
    private long _position = 0;
    private MemoryMappedViewStream? _currentView;
    private long _currentViewStart = -1;
    private const int ViewSize = 65536; // 64 KB

    public SlidingMemoryMappedStream(MemoryMappedFile memoryMappedFile, long offset, long length)
    {
        _memoryMappedFile = memoryMappedFile;
        _position = offset;
        Length = length;
    }

    private void EnsureViewForPosition(long streamPosition)
    {
        var fileOffset = (streamPosition / ViewSize) * ViewSize;

        // If we already have the right view, we're done
        if (_currentView != null && _currentViewStart == fileOffset)
            return;

        // Dispose old view and create new one
        _currentView?.Dispose();

        var remainingStreamLength = Length - fileOffset;
        var viewLength = Math.Min(ViewSize, remainingStreamLength);

        if (viewLength <= 0)
            throw new InvalidOperationException("Cannot create view beyond stream bounds.");

        _currentView = _memoryMappedFile.CreateViewStream(fileOffset, viewLength, MemoryMappedFileAccess.Read);
        _currentViewStart = fileOffset;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length)
                throw new ArgumentOutOfRangeException(nameof(value), "Position must be within the bounds of the stream.");
            _position = value;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count must be within the bounds of the buffer.");

        int totalBytesRead = 0;

        while (count > 0 && _position < Length)
        {
            // Ensure we have a view for the current position
            EnsureViewForPosition(_position);

            // Calculate offset within the current view
            var viewOffset = (int)(_position - _currentViewStart);

            // Position the view stream
            _currentView!.Seek(viewOffset, SeekOrigin.Begin);

            // Calculate how much we can read from this view
            var remainingInView = ViewSize - viewOffset;
            var remainingInStream = Length - _position;
            var bytesToRead = Math.Min(count, Math.Min(remainingInView, remainingInStream));

            if (bytesToRead <= 0)
                break;

            var bytesRead = _currentView.Read(buffer, offset, (int)bytesToRead);
            if (bytesRead == 0)
                break;

            totalBytesRead += bytesRead;
            offset += bytesRead;
            count -= bytesRead;
            _position += bytesRead;

            // If we've read all we can from this view and still have more to read,
            // the next iteration will create the next view
        }

        return totalBytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), "Invalid SeekOrigin value.")
        };

        if (newPosition < 0 || newPosition > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "The resulting position must be within the bounds of the stream.");

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentView?.Dispose();
            _currentView = null;
        }
        base.Dispose(disposing);
    }
}
