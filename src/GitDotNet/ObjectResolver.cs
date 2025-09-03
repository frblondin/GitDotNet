using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Readers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace GitDotNet;

internal delegate IObjectResolver ObjectResolverFactory(string repositoryPath, bool useReadCommitGraph);

/// <summary>Represents a collection of Git objects in a repository.</summary>
internal partial class ObjectResolver : IObjectResolver, IObjectResolverInternal
{
    internal const int HashLength = 20;
    private readonly bool _useReadCommitGraph;
    private readonly IOptions<IGitConnection.Options> _options;
    private readonly LooseReader _looseObjects;
    private readonly Lazy<CommitGraphReader> _commitReader;
    private readonly LfsReader _lfsReader;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<ObjectResolver>? _logger;
    private readonly CancellationTokenSource _disposed = new();
    private bool _disposedValue;

    internal ObjectResolver(string repositoryPath, bool useReadCommitGraph,
        IOptions<IGitConnection.Options> options,
        PackManagerFactory packManagerFactory,
        LooseReaderFactory looseReaderFactory,
        LfsReaderFactory lfsReaderFactory,
        CommitGraphReaderFactory commitReaderFactory,
        IMemoryCache memoryCache,
        IFileSystem fileSystem,
        ILogger<ObjectResolver>? logger = null)
    {
        _logger = logger;
        _logger?.LogDebug("ObjectResolver constructed for repositoryPath={RepositoryPath}, useReadCommitGraph={UseReadCommitGraph}", repositoryPath, useReadCommitGraph);
        Path = fileSystem.Path.Combine(repositoryPath, "objects");
        _useReadCommitGraph = useReadCommitGraph;
        _options = options;
        _looseObjects = looseReaderFactory(Path);
        _commitReader = new(() => commitReaderFactory(Path, this));
        _lfsReader = lfsReaderFactory(fileSystem.Path.Combine(repositoryPath, "lfs", "objects"));
        _memoryCache = memoryCache;
        PackManager = packManagerFactory(Path);
    }

    /// <summary>Gets the path to the Git objects directory.</summary>
    public string Path { get; init; }

    public IPackManager PackManager { get; }

    [ExcludeFromCodeCoverage]
    async Task<byte[]> IObjectResolverInternal.GetDataAsync(HashId id) => (await GetUnlinkedEntryAsync(id, throwIfNotFound: true).ConfigureAwait(false)).Data;

    [ExcludeFromCodeCoverage]
    internal async Task<UnlinkedEntry> GetUnlinkedEntryAsync(HashId id) => await GetUnlinkedEntryAsync(id, throwIfNotFound: true).ConfigureAwait(false);

    internal async Task<UnlinkedEntry> GetUnlinkedEntryAsync(HashId id, bool throwIfNotFound) =>
        (await _memoryCache.GetOrCreateAsync((id, nameof(UnlinkedEntry)),
            async entry => {
                _logger?.LogDebug("Cache miss for UnlinkedEntry {HashId}", id);
                return await ReadUnlinkedEntryAsync(entry, id, throwIfNotFound).ConfigureAwait(false);
            }).ConfigureAwait(false))!;

    private async Task<UnlinkedEntry?> ReadUnlinkedEntryAsync(ICacheEntry entry, HashId id, bool throwIfNotFound)
    {
        _logger?.LogDebug("ReadUnlinkedEntryAsync called for {HashId}, throwIfNotFound={ThrowIfNotFound}", id, throwIfNotFound);
        const int attempts = 1;
        const int delayInMs = 100;

        var result = default(UnlinkedEntry?);
        for (int i = 0; i < attempts; i++)
        {
            result = await ReadUnlinkedEntryAsync(id, throwIfNotFound && i == attempts - 1).ConfigureAwait(false);
            if (result is null && i < attempts - 1)
            {
                // Protect against filesystem changes not being immediately visible
                await Task.Delay(delayInMs).ConfigureAwait(false);
                PackManager.UpdateIndices(force: true);
                continue;
            }
        }

        _options.Value.ApplyTo(entry, result, _disposed.Token);

        // Ensure the hash is correct, it may be null if produced through OFS delta within a pack from offset.
        return result is null || result.Id.Hash.Count > 0 ? result : result with { Id = id };
    }

    private async Task<UnlinkedEntry?> ReadUnlinkedEntryAsync(HashId id, bool throwIfNotFound)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);
        _logger?.LogDebug("ReadUnlinkedEntryAsync (inner) for {HashId}", id);
        // Read loose object
        var hexString = id.ToString();
        var (type, dataProvider, length) = _looseObjects.TryLoad(hexString);
        _logger?.LogDebug("TryLoad result for {HexString}: type={Type}, hasDataProvider={HasDataProvider}, length={Length}", hexString, type, dataProvider != null, length);
        if (dataProvider is not null)
        {
            using var stream = dataProvider();
            return new UnlinkedEntry(type, id, await stream.ToArrayAsync(length).ConfigureAwait(false));
        }
        else
        {
            if (throwIfNotFound)
                _logger?.LogDebug("Object {HexString} not found in loose objects.", hexString);
            return await LoadFromPacksAsync(id, GetDependentObjectAsync, throwIfNotFound).ConfigureAwait(false);
        }
    }

    private async Task<UnlinkedEntry> GetDependentObjectAsync(HashId h) => await GetUnlinkedEntryAsync(h, throwIfNotFound: true).ConfigureAwait(false);

    public async Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry =>
        (await _memoryCache.GetOrCreateAsync((id, typeof(TEntry) == typeof(LogEntry) ? nameof(LogEntry) : nameof(Entry)),
                                             async entry => await ReadAsync<TEntry>(entry, id, true).ConfigureAwait(false)).ConfigureAwait(false))!;

    public async Task<TEntry?> TryGetAsync<TEntry>(HashId id) where TEntry : Entry =>
        await _memoryCache.GetOrCreateAsync((id, typeof(TEntry) == typeof(LogEntry) ? nameof(LogEntry) : nameof(Entry)),
                                            async entry => await ReadAsync<TEntry>(entry, id, false).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task<TEntry?> ReadAsync<TEntry>(ICacheEntry entry, HashId id, bool throwIfNotFound) where TEntry : Entry
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);

        TEntry? result = null;
        if (typeof(TEntry) == typeof(LogEntry))
        {
            result = await ReadLogEntryAsync(id, result).ConfigureAwait(false);
        }
        else
        {
            var unlinked = await GetUnlinkedEntryAsync(id, throwIfNotFound).ConfigureAwait(false);
            result = unlinked is not null ? (TEntry)CreateEntry(unlinked) : null;
        }
        _options.Value.ApplyTo(entry, result, _disposed.Token);

        // Ensure the hash is correct, it may be null if produced through OFS delta within a pack from offset.
        if (result is not null && (result.Id is null || result.Id.Hash.Count == 0))
        {
            result.Id = id;
        }
        return result;
    }

    private async Task<TEntry?> ReadLogEntryAsync<TEntry>(HashId id, TEntry? result) where TEntry : Entry
    {
        if (_useReadCommitGraph && !_commitReader.Value.IsEmpty)
        {
            result = (TEntry?)(object?)_commitReader.Value.Get(id);
        }
        if (result is null)
        {
            var commit = await TryGetAsync<CommitEntry>(id).ConfigureAwait(false);
            if (commit is not null)
            {
                var signature = commit.Committer ??
                    commit.Author ??
                    throw new InvalidOperationException("No signature could be found.");
                result = (TEntry?)(object?)new LogEntry(commit.Id, commit.RootTree,
                    commit.ParentIds, signature.Timestamp, this);
            }
        }

        return result;
    }

    private async Task<UnlinkedEntry?> LoadFromPacksAsync(HashId id, Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider, bool throwIfNotFound)
    {
        var (packIndex, index) = await FindPackAsync(id, throwIfNotFound).ConfigureAwait(false);
        if (packIndex is null) return null;
        return await packIndex.GetAsync(index, id, dependentEntryProvider).ConfigureAwait(false);
    }

    private async Task<(PackIndexReader? packIndexReader, int index)> FindPackAsync(HashId id, bool throwIfNotFound)
    {
        _logger?.LogDebug("FindPackAsync called for {HashId}", id);
        var foundPackIndex = default(PackIndexReader?);
        var foundIndex = -1;
        foreach (var packIndex in PackManager.Indices)
        {
            var index = await packIndex.GetIndexOfAsync(id).ConfigureAwait(false);
            if (index != -1)
            {
                if (id.Hash.Count < HashLength && foundPackIndex is not null)
                {
                    _logger?.LogWarning("Ambiguous hash {HashId} found in multiple packs.", id);
                    throw new AmbiguousHashException();
                }
                foundIndex = index;
                foundPackIndex = packIndex;
                if (id.Hash.Count >= HashLength) break;
            }
        }
        if (foundPackIndex is null && throwIfNotFound)
        {
            _logger?.LogWarning("Hash {HashId} could not be found in any pack.", id);
            throw new KeyNotFoundException($"Hash {id} could not be found.");
        }
        return (foundPackIndex, foundIndex);
    }

    internal Entry CreateEntry(EntryType type, HashId id, byte[] data) => type switch
    {
        EntryType.Commit => new CommitEntry(id, data, this),
        EntryType.Blob => new BlobEntry(id, data, _lfsReader.Load),
        EntryType.Tree => new TreeEntry(id, data, this),
        EntryType.Tag => new TagEntry(id, data, this),
        _ => throw new InvalidOperationException("Unknown object type."),
    };

    internal Entry CreateEntry(UnlinkedEntry entry) =>
        CreateEntry(entry.Type, entry.Id, entry.Data);

    /// <summary>Releases all resources used by the current instance of the <see cref="ObjectResolver"/> class.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _logger?.LogDebug("ObjectResolver disposing managed resources");
                if (_commitReader.IsValueCreated)
                    _commitReader.Value?.Dispose();
                PackManager?.Dispose();
                _logger?.LogInformation("ObjectResolver disposed.");
            }
            if (!(_disposed?.IsCancellationRequested ?? false))
                _disposed!.Cancel();
            _disposed?.Dispose();
            _disposedValue = true;
        }
    }

    /// <summary>Finalizes an instance of the <see cref="GitConnection"/> class.</summary>
    [ExcludeFromCodeCoverage]
    ~ObjectResolver()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Represents a collection of Git objects in a repository.</summary>
public interface IObjectResolver : IDisposable
{
    /// <summary>Retrieves a Git object by its hash.</summary>
    /// <param name="id">The hash of the Git object.</param>
    /// <returns>The Git object associated with the specified hash.</returns>
    Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry;

    /// <summary>Retrieves a Git object by its hash.</summary>
    /// <param name="id">The hash of the Git object.</param>
    /// <returns>The Git object associated with the specified hash.</returns>
    Task<TEntry?> TryGetAsync<TEntry>(HashId id) where TEntry : Entry;
}

internal interface IObjectResolverInternal
{
    IPackManager PackManager { get; }

    Task<byte[]> GetDataAsync(HashId id);

    Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry;
}