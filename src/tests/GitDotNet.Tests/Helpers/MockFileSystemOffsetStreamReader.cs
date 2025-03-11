using System.IO.Abstractions;
using GitDotNet.Tools;

namespace GitDotNet.Tests.Helpers;
internal class MockFileSystemOffsetStreamReader(IFileSystem fileSystem, string path) : IFileOffsetStreamReader
{
    public string Path { get; } = path;

    public Stream OpenRead(long offset)
    {
        var result = fileSystem.File.OpenRead(Path);
        result.Seek(offset, SeekOrigin.Begin);
        return result;
    }

    public void Dispose() { }
}

internal static class FileExtensions
{
    internal static IFileOffsetStreamReader CreateOffsetReader(this IFileSystem fileSystem, string path) =>
        new MockFileSystemOffsetStreamReader(fileSystem, path);
}