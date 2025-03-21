using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Readers;

namespace GitDotNet;

internal delegate Index IndexFactory(string repositoryPath, IObjectResolver objectResolver);

/// <summary>Represents a Git index file.</summary>
[ExcludeFromCodeCoverage]
public class Index : IDisposable
{
    private readonly IndexReader _indexReader;
    private bool _disposedValue;

    internal Index(string repositoryPath, IObjectResolver objectResolver, IndexReaderFactory indexReaderFactory, IFileSystem fileSystem)
    {
        var indexPath = fileSystem.Path.Combine(repositoryPath, "index");
        if (!fileSystem.File.Exists(indexPath))
        {
            throw new FileNotFoundException($"Index file not found: {indexPath}");
        }
        _indexReader = indexReaderFactory(indexPath, objectResolver);
    }

    /// <summary>Gets the entries from the index file asynchronously.</summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="IndexEntry"/> instances.</returns>
    public async Task<IList<IndexEntry>> GetEntriesAsync() => await _indexReader.GetEntriesAsync();

    /// <summary>Disposes the index file.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _indexReader.Dispose();
            }

            _disposedValue = true;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
