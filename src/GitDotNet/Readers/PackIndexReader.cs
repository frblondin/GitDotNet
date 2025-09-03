using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace GitDotNet.Readers;

internal delegate PackIndexReader.Standard StandardPackIndexReaderFactory(string path);
internal delegate PackIndexReader.MultiPack MultiPackIndexReaderFactory(string path);

internal abstract partial class PackIndexReader : IDisposable
{
    /// <summary>Maximum number of object names to load into memory for faster access.</summary>
    private const long MaxInMemoryFileSize = 32_000_000;

    protected const int FanOutTableSize = 256;

    /// <summary>Use a weak reference to allow the offset stream reader to be garbage collected if memory is needed.</summary>
    private readonly WeakReference<IFileOffsetStreamReader> _offsetStreamReader;
    private readonly FileOffsetStreamReaderFactory _offsetStreamReaderFactory;
    protected readonly IFileSystem _fileSystem;
    private readonly ILogger<PackIndexReader>? _logger;
    private bool _disposedValue;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _lock = new();

    private protected PackIndexReader(string path, PackReaderFactory packReaderFactory, FileOffsetStreamReaderFactory offsetStreamReaderFactory, IFileSystem fileSystem, IMemoryCache cache, ILogger<PackIndexReader>? logger = null)
    {
        Path = path;
        _offsetStreamReaderFactory = offsetStreamReaderFactory;
        _fileSystem = fileSystem;
        var size = fileSystem.FileInfo.New(path).Length;
        _offsetStreamReader = new(size > MaxInMemoryFileSize ?
            offsetStreamReaderFactory(path) :
            cache.GetOrCreate(path, e =>
            {
                e.SetSize(size);
                e.SetPriority(CacheItemPriority.Low);
                return new FileInMemoryOffsetStreamReader(path, fileSystem);
            })!);
        _logger = logger;
        _logger?.LogInformation("Loading pack index file: {Path}", path);

        WriteTime = fileSystem.FileInfo.New(path).LastWriteTime;
        using var stream = OffsetStreamReader.OpenRead(0L);
#pragma warning disable S2139 // Exceptions should be either logged or rethrown but not both
        try
        {
#pragma warning disable S1699 // Constructors should only call non-overridable methods
            Version = ReadVersion(stream);
            HashLength = ReadHashLength(stream);
            PackReaders = ReadPacks(stream);
#pragma warning restore S1699 // Constructors should only call non-overridable methods
            Fanout = ReadFanOutTable(stream);

            _logger?.LogInformation("Successfully loaded pack index file: {Path}, version: {Version}, object count: {ObjectCount}", Path, Version, Fanout[FanOutTableSize - 1]);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading pack index file: {Path}", Path);
            throw;
        }
#pragma warning restore S2139 // Exceptions should be either logged or rethrown but not both
    }

    public string Path { get; }

    public IFileOffsetStreamReader OffsetStreamReader
    {
        get
        {
            lock (_lock)
            {
                if (!_offsetStreamReader.TryGetTarget(out var reader))
                {
                    reader = _offsetStreamReaderFactory(Path);
                    _offsetStreamReader.SetTarget(reader);
                }
                return reader;
            }
        }
    }

    public int Version { get; private set; }

    public int[] Fanout { get; private set; }

    public int HashLength { get; private set; }

    public int Count => Fanout[FanOutTableSize - 1];

    internal bool IsObsolete { get; set; }

    protected abstract int HeaderLength { get; }

    protected abstract long FanOutTableOffset { get; }

    protected abstract long SortedObjectNamesOffset { get; }

    protected abstract long PackFilePositionOffset { get; }

    protected abstract long PackFilePositionLongOffset { get; }

    internal IList<(string Path, Lazy<PackReader> Reader)> PackReaders { get; }

    public DateTime WriteTime { get; }

    public bool HasBeenModified =>
        !_fileSystem.File.Exists(Path) ||
        WriteTime != _fileSystem.FileInfo.New(Path).LastWriteTime;

    private int[] ReadFanOutTable(Stream stream)
    {
        stream.Seek(FanOutTableOffset, SeekOrigin.Begin);
        var bytes = new byte[4];
        var result = new int[FanOutTableSize];
        for (var i = 0; i < FanOutTableSize; i++)
        {
            stream.ReadExactly(bytes);
            result[i] = BinaryPrimitives.ReadInt32BigEndian(bytes);
        }
        return result;
    }

    protected abstract int ReadVersion(Stream stream);

    protected abstract int ReadHashLength(Stream stream);

    protected abstract IList<(string Path, Lazy<PackReader> Reader)> ReadPacks(Stream stream);

    public async Task<HashId> GetHashAsync(int index)
    {
        using var stream = OpenSortedObjectNameStream(index);
        return await GetHashAsync(stream).ConfigureAwait(false);
    }

    private Stream OpenSortedObjectNameStream(int index) => OffsetStreamReader.OpenRead(SortedObjectNamesOffset + index * HashLength);

    private async Task<HashId> GetHashAsync(Stream stream)
    {
        var hash = new byte[HashLength];
        await stream.ReadExactlyAsync(hash.AsMemory(0, HashLength)).ConfigureAwait(false);
        return hash;
    }

    public async Task<int> GetIndexOfAsync(HashId id)
    {
        using var stream = OffsetStreamReader.OpenRead(0L);
        var hashBuffer = new byte[HashLength];
        var fanoutPosition = id.Hash[0];
        var end = Fanout[fanoutPosition];
        var start = fanoutPosition > 0 ? Fanout[fanoutPosition - 1] : 0;

        var index = await FindHashIndexAsync(id, GetHashInSortedObjectNames, end, start).ConfigureAwait(false);
        if (index != -1 && id.Hash.Count < HashLength)
        {
            await CheckForAmbiguousHash(id, GetHashInSortedObjectNames,
                Math.Max(0, index - 1), Math.Min(end, index + 1), index).ConfigureAwait(false);
        }
        return index;

        async Task<byte[]> GetHashInSortedObjectNames(int index)
        {
            stream.Seek(SortedObjectNamesOffset + index * HashLength, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(hashBuffer).ConfigureAwait(false);
            return hashBuffer;
        }
    }

    private static async Task CheckForAmbiguousHash(HashId id, Func<int, Task<byte[]>> objectRetrieval, int start, int end, int alreadyFound)
    {
        for (var i = start; i < end; i++)
        {
            var hash = await objectRetrieval(i).ConfigureAwait(false);
            AmbiguousHashException.CheckForAmbiguousHashMatch(id, alreadyFound, hash, i);
        }
    }

    private static async Task<int> FindHashIndexAsync(HashId id, Func<int, Task<byte[]>> objectRetrieval, int end, int start)
    {
        var result = -1;
        while (start < end)
        {
            var mid = (start + end) / 2;
            var hash = await objectRetrieval(mid).ConfigureAwait(false);
            var comparison = id.CompareTo(hash.AsSpan(0, id.Hash.Count));
            if (comparison == 0)
            {
                result = mid;
                break;
            }
            else if (comparison < 0)
            {
                end = mid;
            }
            else
            {
                start = mid + 1;
            }
        }

        return result;
    }

    public async Task<UnlinkedEntry> GetAsync(int index, HashId id, Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        using var stream = OffsetStreamReader.OpenRead(0);
        var (reader, offset) = await GetPackFileOffsetAsync(index, stream).ConfigureAwait(false);

        return await reader.GetByOffsetAsync(offset, async () => await reader.ReadAsync(id, offset, dependentEntryProvider).ConfigureAwait(false)).ConfigureAwait(false);
    }

    protected abstract Task<(PackReader, long)> GetPackFileOffsetAsync(int index, Stream stream);

    [ExcludeFromCodeCoverage]
    protected async Task<long> CalculateLargePackFileOffset(int index, Stream stream, byte[] offset, long result)
    {
        if (result > 0x80000000)
        {
            if (PackFilePositionLongOffset < 0)
            {
                throw new InvalidDataException("Pack file offset is too large, but no long offset table exists.");
            }

            stream.Seek(PackFilePositionLongOffset + index * 8, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(offset.AsMemory(0, 8)).ConfigureAwait(false);
            result = BinaryPrimitives.ReadInt32BigEndian(offset.AsSpan(0, 4));
        }

        return result;
    }

    public async IAsyncEnumerable<(int Position, HashId Id)> GetHashesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(PackReader));

        using var stream = OpenSortedObjectNameStream(0);
        for (int i = 0; i < Count; i++)
        {
            yield return (i, await GetHashAsync(stream).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Cleans up resources used by the object, allowing for both managed and unmanaged resource disposal.
    /// </summary>
    /// <param name="disposing">Indicates whether to release both managed and unmanaged resources or just unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                IsObsolete = true;
                if (!_disposed.IsCancellationRequested)
                    _disposed.Cancel();
                _disposed.Dispose();
                if (_offsetStreamReader.TryGetTarget(out var reader))
                {
                    reader.Dispose();
                }
                DisposePackReaders();
            }

            _disposedValue = true;
        }
    }

    private void DisposePackReaders()
    {
        foreach (var reader in from r in PackReaders
                               where r.Reader.IsValueCreated
                               select r.Reader.Value)
        {
            reader.Dispose();
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}