using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;

namespace GitDotNet.Tests.Helpers;
internal static class MockFileSystemExtensions
{
    internal static MockFileSystem AddZipContent(this MockFileSystem fileSystem, byte[] data)
    {
        using var memoryStream = new MemoryStream(data);
        using var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
        foreach (var entry in zipArchive.Entries)
        {
            if (entry.FullName.EndsWith("/"))
            {
                // It's a directory, create it in the mock file system
                fileSystem.AddDirectory(entry.FullName);
            }
            else
            {
                // It's a file, read the content and add it to the mock file system
                using var stream = entry.Open();
                using var entryMemoryStream = new MemoryStream();
                stream.CopyTo(entryMemoryStream);
                fileSystem.AddFile(entry.FullName, new MockFileData(entryMemoryStream.ToArray()));
            }
        }
        return fileSystem;
    }
}
