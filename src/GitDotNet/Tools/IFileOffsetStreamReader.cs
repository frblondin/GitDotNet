using System.IO.Abstractions;
using System.IO.MemoryMappedFiles;
using GitDotNet.Readers;

namespace GitDotNet.Tools;

internal delegate IFileOffsetStreamReader FileOffsetStreamReaderFactory(string path);

internal interface IFileOffsetStreamReader : IDisposable
{
    string Path { get; }

    Stream OpenRead(long offset);
}

internal class FileOffsetStreamReader(string path, IFileSystem fileSystem) : IFileOffsetStreamReader
{
    private readonly MemoryMappedFile _memoryMappedFile = MemoryMappedFile.CreateFromFile(path, System.IO.FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
    private readonly long _length = fileSystem.FileInfo.New(path).Length;
    private bool _disposedValue;

    public string Path { get; } = path;

    public Stream OpenRead(long offset) => new SlidingMemoryMappedStream(_memoryMappedFile, offset, _length);

    /// <summary>Cleans up resources, optionally releasing managed resources based on the provided flag.</summary>
    /// <param name="disposing">Indicates whether to release both managed and unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _memoryMappedFile.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}