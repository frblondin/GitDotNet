using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Readers;
using GitDotNet.Tools;

namespace GitDotNet;

internal delegate Index IndexFactory(RepositoryInfo info, IObjectResolver objectResolver, ConnectionPool.Lock repositoryLocker);

/// <summary>Represents a Git index file.</summary>
[ExcludeFromCodeCoverage]
public class Index
{
    private readonly IRepositoryInfo _info;
    private readonly IndexReader _indexReader;
    private readonly ConnectionPool.Lock _repositoryLocker;
    private readonly IFileSystem _fileSystem;

    internal Index(IRepositoryInfo info, IObjectResolver objectResolver, ConnectionPool.Lock repositoryLocker, IndexReaderFactory indexReaderFactory, IFileSystem fileSystem)
    {
        var indexPath = fileSystem.Path.Combine(info.Path, "index");
        _info = info;
        _indexReader = indexReaderFactory(indexPath, objectResolver);
        _repositoryLocker = repositoryLocker;
        _fileSystem = fileSystem;
    }

    /// <summary>Gets the entries from the index file asynchronously.</summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="IndexEntry"/> instances.</returns>
    public async Task<IImmutableList<IndexEntry>> GetEntriesAsync() => await _indexReader.GetEntriesAsync();

    /// <summary>Adds a new entry to the Git index using a byte array as content.</summary>
    /// <param name="data">The byte array containing the data to be added to the Git index.</param>
    /// <param name="path">Specifies the path in the repository where the new entry will be added.</param>
    /// <param name="mode">Indicates the file mode for the new entry in the Git index.</param>
    /// <exception cref="InvalidOperationException">Thrown when the creation of a blob using the git hash-object command fails.</exception>
    public void AddEntry(byte[] data, GitPath path, FileMode mode) => _repositoryLocker.ExecuteWithTemporaryLockReleaseAsync(() =>
    {
        var tempFilePath = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), _fileSystem.Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tempFilePath, data);

            // Use git hash-object to create a blob from the content of the temporary file
            string? blobHash = null;
            GitCliCommand.Execute(_info.Path, $"hash-object -w \"{tempFilePath}\"", outputDataReceived: (_, e) =>
            {
                if (e.Data is not null) blobHash = e.Data.Trim();
            });

            if (blobHash is null) throw new InvalidOperationException("Failed to create blob using git hash-object.");

            GitCliCommand.Execute(_info.Path, $"update-index --add --cacheinfo {mode},{blobHash},\"{path}\"");
        }
        finally
        {
            File.Delete(tempFilePath);
        }
        return Task.CompletedTask;
    }).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Add file contents to the index.</summary>
    /// <param name="pattern">Files to add content from.</param>
    /// <param name="overrideExecutable">Overrides the executable bit of the added files. The executable bit is only changed in the index, the files on disk are left unchanged.</param>
    public void AddEntries(string pattern, bool overrideExecutable = false) => _repositoryLocker.ExecuteWithTemporaryLockReleaseAsync(() =>
    {
        GitCliCommand.Execute(_info.RootFilePath, $"add {pattern} {(overrideExecutable ? " --chmod=+x" : "")}");
        return Task.CompletedTask;
    }).ConfigureAwait(false).GetAwaiter().GetResult();
}
