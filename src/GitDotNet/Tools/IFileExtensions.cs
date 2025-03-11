namespace System.IO.Abstractions;

internal static class IFileExtensions
{
    internal static FileSystemStream OpenReadAsynchronous(this IFile file, string path) =>
        file.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous,
        });
}
