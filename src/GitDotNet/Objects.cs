using GitDotNet.Readers;
using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;

namespace GitDotNet;

internal delegate Objects ObjectsFactory(string repositoryPath, bool useReadCommitGraph);

/// <summary>Represents a collection of Git objects in a repository.</summary>
public partial class Objects : IDisposable, IObjectResolver
{
    internal const int HashLength = 20;
    private readonly bool _useReadCommitGraph;
    private readonly IOptions<GitConnection.Options> _options;
    private readonly LooseReader _looseObjects;
    private readonly PackReaderFactory _packReaderFactory;
    private readonly Lazy<CommitGraphReader?> _commitReader;
    private readonly LfsReader _lfsReader;
    private readonly IMemoryCache _memoryCache;
    private readonly IFileSystem _fileSystem;
    private readonly CancellationTokenSource _disposed = new();
    private bool _disposedValue;

    internal Objects(string repositoryPath, bool useReadCommitGraph,
                     IOptions<GitConnection.Options> options,
                     LooseReaderFactory looseReaderFactory,
                     PackReaderFactory packReaderFactory,
                     LfsReaderFactory lfsReaderFactory,
                     CommitGraphReaderFactory commitReaderFactory,
                     IMemoryCache memoryCache,
                     IFileSystem fileSystem)
    {
        Path = fileSystem.Path.Combine(repositoryPath, "objects");
        _fileSystem = fileSystem;
        _useReadCommitGraph = useReadCommitGraph;
        _options = options;
        _looseObjects = looseReaderFactory(Path);
        _packReaderFactory = packReaderFactory;
        _commitReader = new(() => commitReaderFactory(Path, this));
        _lfsReader = lfsReaderFactory(fileSystem.Path.Combine(repositoryPath, "lfs", "objects"));
        _memoryCache = memoryCache;

        PackReaders = ReinitializePacks();
    }

    internal ImmutableDictionary<string, Lazy<PackReader>> PackReaders { get; private set; }

    /// <summary>Gets the path to the Git objects directory.</summary>
    public string Path { get; init; }

    internal ImmutableDictionary<string, Lazy<PackReader>> ReinitializePacks()
    {
        DisposePacks();

        var packDir = _fileSystem.Path.Combine(Path, "pack");
        var result = ImmutableDictionary.CreateBuilder<string, Lazy<PackReader>>();
        if (_fileSystem.Directory.Exists(packDir))
        {
            foreach (var packFile in _fileSystem.Directory.GetFiles(packDir, "*.pack"))
            {
                result[_fileSystem.Path.GetFileNameWithoutExtension(packFile)] =
                    new(() => _packReaderFactory(packFile));
            }
        }
        return PackReaders = result.ToImmutable();
    }

    [ExcludeFromCodeCoverage]
    async Task<byte[]> IObjectResolver.GetDataAsync(HashId id) => (await GetUnlinkedEntryAsync(id, throwIfNotFound: true)).Data;

    [ExcludeFromCodeCoverage]
    internal async Task<UnlinkedEntry> GetUnlinkedEntryAsync(HashId id) => await GetUnlinkedEntryAsync(id, throwIfNotFound: true);

    internal async Task<UnlinkedEntry> GetUnlinkedEntryAsync(HashId id, bool throwIfNotFound) =>
        (await _memoryCache.GetOrCreateAsync((id, nameof(UnlinkedEntry)),
                                             async entry => await ReadUnlinkedEntryAsync(entry, id, throwIfNotFound)))!;

    private async Task<UnlinkedEntry?> ReadUnlinkedEntryAsync(ICacheEntry entry, HashId id, bool throwIfNotFound)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(Objects));

        // Read loose object
        var hexString = id.ToString();
        var (type, dataProvider, length) = _looseObjects.TryLoad(hexString);
        UnlinkedEntry? result;
        if (dataProvider is not null)
        {
            using var stream = dataProvider();
            result = new UnlinkedEntry(type, id, await stream.ToArrayAsync(length));
        }
        else
        {
            result = await LoadFromPacksAsync(id, GetDependentObjectAsync, throwIfNotFound);
        }
        _options.Value.ApplyTo(entry, result, _disposed.Token);

        // Ensure the hash is correct, it may be null if produced through OFS delta within a pack from offset.
        return result is null || result.Id.Hash.Count > 0 ? result : result with { Id = id };

        async Task<UnlinkedEntry> GetDependentObjectAsync(HashId h) => await GetUnlinkedEntryAsync(h, throwIfNotFound: true);
    }

    /// <summary>Retrieves a Git object by its hash.</summary>
    /// <param name="id">The hash of the Git object.</param>
    /// <returns>The Git object associated with the specified hash.</returns>
    public async Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry =>
        await GetAsync<TEntry>(id, throwIfNotFound: true);

    /// <summary>Retrieves a Git object by its hash.</summary>
    /// <param name="id">The hash of the Git object.</param>
    /// <param name="throwIfNotFound">A value indicating whether to throw an exception if the object is not found.</param>
    /// <returns>The Git object associated with the specified hash.</returns>
    public async Task<TEntry> GetAsync<TEntry>(HashId id, bool throwIfNotFound) where TEntry : Entry =>
        (await _memoryCache.GetOrCreateAsync((id, nameof(Entry)),
                                             async entry => await ReadAsync<TEntry>(entry, id, throwIfNotFound)))!;

    /// <summary>Retrieves a Git object by its hash.</summary>
    /// <param name="hash">The hash of the Git object as a hexadecimal string.</param>
    /// <returns>The Git object associated with the specified hash.</returns>
    public async Task<TEntry> GetAsync<TEntry>(string hash) where TEntry : Entry =>
        await GetAsync<TEntry>(hash.HexToByteArray(), throwIfNotFound: true);

    /// <summary>Retrieves a Git object by its hash.</summary>
    /// <param name="hash">The hash of the Git object as a hexadecimal string.</param>
    /// <param name="throwIfNotFound">A value indicating whether to throw an exception if the object is not found.</param>
    /// <returns>The Git object associated with the specified hash.</returns>
    public async Task<TEntry> GetAsync<TEntry>(string hash, bool throwIfNotFound) where TEntry : Entry =>
        await GetAsync<TEntry>(hash.HexToByteArray(), throwIfNotFound);

    private async Task<TEntry?> ReadAsync<TEntry>(ICacheEntry entry, HashId id, bool throwIfNotFound) where TEntry : Entry
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(Objects));

        TEntry? result = null;
        if (typeof(TEntry) == typeof(CommitEntry) && _useReadCommitGraph && _commitReader.Value is not null)
        {
            result = (TEntry?)(object?)_commitReader.Value.Get(id);
        }
        if (result is null)
        {
            var unlinked = await GetUnlinkedEntryAsync(id, throwIfNotFound);
            result = unlinked is not null ? (TEntry)CreateEntry(unlinked) : null;
        }
        _options.Value.ApplyTo(entry, result, _disposed.Token);

        // Ensure the hash is correct, it may be null if produced through OFS delta within a pack from offset.
        return result is null || result.Id.Hash.Count > 0 ? result : result with { Id = id };
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
        foreach (var pack in PackReaders.Values.Select(p => p.Value))
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
        if (foundPack is null && throwIfNotFound) throw new KeyNotFoundException($"Hash {id} not found in any pack.");
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

    /// <summary>Releases all resources used by the current instance of the <see cref="Objects"/> class.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                if (!_disposed.IsCancellationRequested)
                    _disposed.Cancel();
                _disposed.Dispose();
                if (_commitReader.IsValueCreated)
                    _commitReader.Value?.Dispose();
                DisposePacks();
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

    private void DisposePacks()
    {
        foreach (var pack in PackReaders?.Values ?? [])
        {
            if (pack.IsValueCreated) pack.Value.Dispose();
        }
    }
}

internal interface IObjectResolver
{
    Task<byte[]> GetDataAsync(HashId id);

    Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry;
}