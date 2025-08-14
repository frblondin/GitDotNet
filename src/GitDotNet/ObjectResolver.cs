using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Readers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GitDotNet;

internal delegate IObjectResolver ObjectResolverFactory(string repositoryPath, bool useReadCommitGraph);

/// <summary>Represents a collection of Git objects in a repository.</summary>
internal partial class ObjectResolver : IObjectResolver, IObjectResolverInternal
{
    internal const int HashLength = 20;
    private readonly bool _useReadCommitGraph;
    private readonly IOptions<GitConnection.Options> _options;
    private readonly LooseReader _looseObjects;
    private readonly Lazy<CommitGraphReader?> _commitReader;
    private readonly LfsReader _lfsReader;
    private readonly IMemoryCache _memoryCache;
    private readonly CancellationTokenSource _disposed = new();
    private bool _disposedValue;

    internal ObjectResolver(string repositoryPath, bool useReadCommitGraph,
                            IOptions<GitConnection.Options> options,
                            PackManagerFactory packManagerFactory,
                            LooseReaderFactory looseReaderFactory,
                            LfsReaderFactory lfsReaderFactory,
                            CommitGraphReaderFactory commitReaderFactory,
                            IMemoryCache memoryCache,
                            IFileSystem fileSystem)
    {
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
    async Task<byte[]> IObjectResolverInternal.GetDataAsync(HashId id) => (await GetUnlinkedEntryAsync(id, throwIfNotFound: true)).Data;

    [ExcludeFromCodeCoverage]
    internal async Task<UnlinkedEntry> GetUnlinkedEntryAsync(HashId id) => await GetUnlinkedEntryAsync(id, throwIfNotFound: true);

    internal async Task<UnlinkedEntry> GetUnlinkedEntryAsync(HashId id, bool throwIfNotFound) =>
        (await _memoryCache.GetOrCreateAsync((id, nameof(UnlinkedEntry)),
            async entry => await ReadUnlinkedEntryAsync(entry, id, throwIfNotFound)))!;

    private async Task<UnlinkedEntry?> ReadUnlinkedEntryAsync(ICacheEntry entry, HashId id, bool throwIfNotFound)
    {
        const int attempts = 3;
        const int delayInMs = 100;

        var result = default(UnlinkedEntry?);
        for (int i = 0; i < attempts; i++)
        {
            result = await ReadUnlinkedEntryAsync(id, throwIfNotFound && i == attempts - 1);
            if (result is null && i < attempts - 1)
            {
                // Protect against filesystem changes not being immediately visible
                await Task.Delay(delayInMs);
                PackManager.UpdatePacks(force: true);
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

        // Read loose object
        var hexString = id.ToString();
        var (type, dataProvider, length) = _looseObjects.TryLoad(hexString);
        if (dataProvider is not null)
        {
            using var stream = dataProvider();
            return new UnlinkedEntry(type, id, await stream.ToArrayAsync(length));
        }
        else
        {
            return await LoadFromPacksAsync(id, GetDependentObjectAsync, throwIfNotFound);
        }
    }

    private async Task<UnlinkedEntry> GetDependentObjectAsync(HashId h) => await GetUnlinkedEntryAsync(h, throwIfNotFound: true);

    public async Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry =>
        (await _memoryCache.GetOrCreateAsync((id, typeof(TEntry) == typeof(LogEntry) ? nameof(LogEntry) : nameof(Entry)),
                                             async entry => await ReadAsync<TEntry>(entry, id, true)))!;

    public async Task<TEntry?> TryGetAsync<TEntry>(HashId id) where TEntry : Entry =>
        await _memoryCache.GetOrCreateAsync((id, typeof(TEntry) == typeof(LogEntry) ? nameof(LogEntry) : nameof(Entry)),
                                            async entry => await ReadAsync<TEntry>(entry, id, false));


    private async Task<TEntry?> ReadAsync<TEntry>(ICacheEntry entry, HashId id, bool throwIfNotFound) where TEntry : Entry
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);

        TEntry? result = null;
        if (typeof(TEntry) == typeof(LogEntry))
        {
            result = await ReadLogEntryAsync(id, result);
        }
        else
        {
            var unlinked = await GetUnlinkedEntryAsync(id, throwIfNotFound);
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
        if (_useReadCommitGraph && _commitReader.Value is not null)
        {
            result = (TEntry?)(object?)_commitReader.Value.Get(id);
        }
        if (result is null)
        {
            var commit = await TryGetAsync<CommitEntry>(id);
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
        var (pack, index) = await FindPackAsync(id, throwIfNotFound);
        if (pack is null) return null;
        return await pack.GetAsync(index, id, dependentEntryProvider);
    }

    private async Task<(PackReader? pack, int index)> FindPackAsync(HashId id, bool throwIfNotFound)
    {
        var foundPack = default(PackReader?);
        var foundIndex = -1;
        foreach (var pack in PackManager.PackReaders)
        {
            var index = await pack.IndexOfAsync(id);
            if (index != -1)
            {
                if (id.Hash.Count < HashLength && foundPack is not null) throw new AmbiguousHashException();
                foundPack = pack;
                foundIndex = index;
                if (id.Hash.Count >= HashLength) return (foundPack, foundIndex);
            }
        }
        if (foundPack is null && throwIfNotFound) throw new KeyNotFoundException($"Hash {id} could not be found.");
        return (foundPack, foundIndex);
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
                if (_commitReader.IsValueCreated)
                    _commitReader.Value?.Dispose();
                PackManager?.Dispose();
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