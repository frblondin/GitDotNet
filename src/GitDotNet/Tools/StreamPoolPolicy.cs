using System.IO.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace GitDotNet.Tools;

internal class StreamPoolPolicy(string Path, IFileSystem FileSystem) : IPooledObjectPolicy<FileSystemStream>
{
    public FileSystemStream Create() => FileSystem.File.OpenReadAsynchronous(Path);

    public bool Return(FileSystemStream obj) => obj.CanRead; // Don't keep using object if disposed
}
