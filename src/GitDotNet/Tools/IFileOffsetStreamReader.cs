using System.IO.MemoryMappedFiles;
using GitDotNet.Readers;

namespace GitDotNet.Tools;

internal delegate IFileOffsetStreamReader FileOffsetStreamReaderFactory(string path);

internal interface IFileOffsetStreamReader : IDisposable
{
    string Path { get; }

    Stream OpenRead(long offset);
}

internal class FileOffsetStreamReader(string path, long length) : IFileOffsetStreamReader
{
    private readonly MemoryMappedFile _memoryMappedFile = MemoryMappedFile.CreateFromFile(path, System.IO.FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

    public string Path { get; } = path;

    public Stream OpenRead(long offset) => new SlidingMemoryMappedStream(_memoryMappedFile, offset, length);

    public void Dispose() => _memoryMappedFile.Dispose();
}