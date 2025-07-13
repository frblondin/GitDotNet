namespace System.IO.Abstractions;

internal static class IFileExtensions
{
    internal static FileSystemStream OpenReadAsynchronous(this IFile file, string path) =>
        file.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite, // Allow concurrent reads/writes, fail if file is being written
            BufferSize = 4096,
            Options = FileOptions.Asynchronous,
        });

    internal static string[] ReadAllLinesShared(this IFile file, string path)
    {
        using var stream = file.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 4096,
            Options = FileOptions.None,
        });
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (!reader.EndOfStream)
            lines.Add(reader.ReadLine()!);
        return lines.ToArray();
    }

    internal static string ReadAllTextShared(this IFile file, string path)
    {
        using var stream = file.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 4096,
            Options = FileOptions.None,
        });
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static byte[] ReadAllBytesShared(this IFile file, string path)
    {
        using var stream = file.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 4096,
            Options = FileOptions.None,
        });
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
