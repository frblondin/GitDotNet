using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;

namespace GitDotNet.Readers;

[ExcludeFromCodeCoverage]
internal class SlidingMemoryMappedStream : Stream
{
    private readonly MemoryMappedFile _memoryMappedFile;
    private readonly long _initialOffset;
    private readonly List<MemoryMappedViewStream> _views = new();
    private long _position;
    private const int ViewSize = 65536; // 64 KB

    public SlidingMemoryMappedStream(MemoryMappedFile memoryMappedFile, long offset, long length)
    {
        _memoryMappedFile = memoryMappedFile;
        _initialOffset = offset;
        _position = 0;
        Length = length;
        CreateView(_initialOffset);
    }

    private void CreateView(long offset)
    {
        var view = _memoryMappedFile.CreateViewStream(offset,
                                                      Math.Min(ViewSize, Length - offset),
                                                      MemoryMappedFileAccess.Read);
        _views.Add(view);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value >= Length)
                throw new ArgumentOutOfRangeException(nameof(value), "Position must be within the length of the stream.");
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
            var viewIndex = (int)(_position / ViewSize);
            var viewOffset = (int)(_position % ViewSize);

            if (viewIndex >= _views.Count)
            {
                CreateView(_initialOffset + viewIndex * ViewSize);
            }

            var view = _views[viewIndex];
            view.Seek(viewOffset, SeekOrigin.Begin);

            var bytesRead = view.Read(buffer, offset, Math.Min(count, ViewSize - viewOffset));
            if (bytesRead == 0)
                break;

            totalBytesRead += bytesRead;
            offset += bytesRead;
            count -= bytesRead;
            _position += bytesRead;
        }

        return totalBytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var view in _views)
            {
                view.Dispose();
            }
            _views.Clear();
        }
        base.Dispose(disposing);
    }
}
