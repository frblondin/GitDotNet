using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Readers;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet;

internal delegate Index IndexFactory(RepositoryInfo info, IObjectResolver objectResolver);

/// <summary>Represents a Git index file.</summary>
[ExcludeFromCodeCoverage]
public class Index
{
    private readonly IRepositoryInfo _info;
    private readonly IndexReader _indexReader;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<Index>? _logger;

    internal Index(IRepositoryInfo info, IObjectResolver objectResolver, IndexReaderFactory indexReaderFactory, IFileSystem fileSystem, ILogger<Index>? logger = null)
    {
        var indexPath = fileSystem.Path.Combine(info.Path, "index");
        _info = info;
        _indexReader = indexReaderFactory(indexPath, objectResolver);
        _fileSystem = fileSystem;
        _logger = logger;
        _logger?.LogDebug("Index initialized for path: {Path}", info.Path);
    }

    /// <summary>Gets the entries from the index file asynchronously.</summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="IndexEntry"/> instances.</returns>
    public async Task<IImmutableList<IndexEntry>> GetEntriesAsync()
    {
        _logger?.LogInformation("Getting index entries asynchronously.");
        var entries = await _indexReader.GetEntriesAsync().ConfigureAwait(false);
        _logger?.LogDebug("Retrieved {Count} index entries.", entries.Count);
        return entries;
    }

    /// <summary>Adds a new entry to the Git index using a byte array as content.</summary>
    /// <param name="data">The byte array containing the data to be added to the Git index.</param>
    /// <param name="path">Specifies the path in the repository where the new entry will be added.</param>
    /// <param name="mode">Indicates the file mode for the new entry in the Git index.</param>
    /// <exception cref="InvalidOperationException">Thrown when the creation of a blob using the git hash-object command fails.</exception>
    public void AddEntry(byte[] data, GitPath path, FileMode mode)
    {
        _logger?.LogInformation("Adding entry to index: {Path} with mode {Mode}", path, mode);
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
            if (blobHash is null)
            {
                _logger?.LogWarning("Failed to create blob using git hash-object.");
                throw new InvalidOperationException("Failed to create blob using git hash-object.");
            }
            GitCliCommand.Execute(_info.Path, $"update-index --add --cacheinfo {mode},{blobHash},\"{path}\"");
            _logger?.LogDebug("Entry added to index: {Path} with blob hash {BlobHash}", path, blobHash);
        }
        finally
        {
            File.Delete(tempFilePath);
        }
    }

    /// <summary>Add file contents to the index.</summary>
    /// <param name="pattern">Files to add content from.</param>
    /// <param name="overrideExecutable">Overrides the executable bit of the added files. The executable bit is only changed in the index, the files on disk are left unchanged.</param>
    public void AddEntries(string pattern, bool overrideExecutable = false)
    {
        _logger?.LogInformation("Adding entries to index with pattern: {Pattern}, overrideExecutable: {OverrideExecutable}", pattern, overrideExecutable);
        GitCliCommand.Execute(_info.RootFilePath, $"add {pattern} {(overrideExecutable ? " --chmod=+x" : "")}");
    }
}
