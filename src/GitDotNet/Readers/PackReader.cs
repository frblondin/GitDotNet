using GitDotNet.Tools;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace GitDotNet.Readers;

internal delegate PackReader PackReaderFactory(string path);

internal class PackReader(string path, FileOffsetStreamReaderFactory offsetStreamReaderFactory, ILogger<PackReader>? logger = null) : IDisposable
{
    private readonly IFileOffsetStreamReader _offsetStreamReader = offsetStreamReaderFactory(path);
    private readonly ConcurrentDictionary<long, Task<UnlinkedEntry>> _cache = new();
    private bool _disposedValue;
    private readonly CancellationTokenSource _disposed = new();

    private async Task<UnlinkedEntry> GetAsync(HashId id,
        long offset,
        Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider) =>
        await GetByOffsetAsync(offset, async () => await ReadAsync(id, offset, dependentEntryProvider).ConfigureAwait(false)).ConfigureAwait(false);

    public Task<UnlinkedEntry> GetByOffsetAsync(long offset, Func<Task<UnlinkedEntry>> provider) =>
        _cache.TryGetValue(offset, out var result) ?
        result :
        _cache.GetOrAdd(offset, provider());

    internal async Task<UnlinkedEntry> ReadAsync(HashId id,
                                                long offset,
                                                Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(PackReader));

        using var stream = _offsetStreamReader.OpenRead(offset);
        return await ReadAsync(stream, id, offset, dependentEntryProvider).ConfigureAwait(false);
    }

    private async Task<UnlinkedEntry> ReadAsync(Stream stream,
                                                HashId id,
                                                long offset,
                                                Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
#if FILL_OFFSET_HASH_WHEN_MISSING
        if ((hash?.Length ?? 0) == 0)
        {
            var indexReader = await EnsureIndex().ConfigureAwait(false);
            hash = await indexReader.GetHashAsync(offset).ConfigureAwait(false);
        }
#endif

        int byteValue = stream.ReadNonEosByteOrThrow();

        // Extract the type (3 bits) and the initial length part (4 bits)
        var type = (EntryType)(byteValue >> 4 & 0x07);
        var shift = 4;
        long length = byteValue & 0x0F; // 0x0F = 0000 1111
        while ((byteValue & 0x80) != 0) // 0x80 = 1000 0000
        {
            byteValue = stream.ReadNonEosByteOrThrow();
            // Only add the lower 7 bits to the length
            length += (long)(byteValue & 0x7F) << shift; // 0x7F = 0111 1111
            shift += 7;
        }
        return type switch
        {
            >= EntryType.Commit and <= EntryType.Tag =>
                new(type, id, await ReadDataAsync(stream, length).ConfigureAwait(false)),
            EntryType.RefDelta =>
                // Lazy load is not possible since we need to read base objects to get type
                await ReconstructRefDeltaAsync(stream, dependentEntryProvider).ConfigureAwait(false),
            EntryType.OfsDelta =>
                // Lazy load is not possible since we need to read base objects to get type
                await ReconstructOfsDeltaAsync(stream, id, offset, dependentEntryProvider).ConfigureAwait(false),
            _ => throw new NotImplementedException($"Unknown type {(int)type} while getting object."),
        };
        static async Task<byte[]> ReadDataAsync(Stream stream, long length)
        {
            var result = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            return await result.ToArrayAsync(length).ConfigureAwait(false);
        }
    }

    private async Task<UnlinkedEntry> ReconstructOfsDeltaAsync(Stream stream,
                                                               HashId id,
                                                               long offset,
                                                               Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        var baseObjectOffset = ExtractOffset(stream);
        var baseObject = await GetAsync(HashId.Empty, offset - baseObjectOffset, dependentEntryProvider).ConfigureAwait(false);

        var data = await ReconstructDeltaAsync(stream, baseObject.Data).ConfigureAwait(false);
        return new(baseObject.Type, id, data);
    }

    [ExcludeFromCodeCoverage]
    private static async Task<UnlinkedEntry> ReconstructRefDeltaAsync(Stream stream, Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        var hash = new byte[20];
        var length = await stream.ReadAsync(hash).ConfigureAwait(false);
        if (length != hash.Length)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading ref delta hash.");
        }
        var baseObject = await dependentEntryProvider(hash).ConfigureAwait(false);

        var data = await ReconstructDeltaAsync(stream, baseObject.Data).ConfigureAwait(false);
        return new(baseObject.Type, hash, data);
    }

    private static async Task<byte[]> ReconstructDeltaAsync(Stream stream, byte[] baseObject)
    {
        using var decompressed = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
        ExtractOffset(decompressed); // Base object size
        ExtractOffset(decompressed); // Target object size

        using var result = new PooledMemoryStream();
        while (true)
        {
            // Read the first byte to determine the instruction type
            int instruction = decompressed.ReadByte();

            if (instruction == -1) break;

            // Check if it's a copy instruction (1xxxxxxx)
            if ((instruction & 0x80) != 0)
            {
                await ReadCopyInstructionAsync(decompressed, instruction, baseObject, result).ConfigureAwait(false);
            }
            else if (instruction != 0)
            {
                // It's an add new data instruction (0xxxxxxx)
                await ReadAddNewDataInstructionAsync(decompressed, instruction, result).ConfigureAwait(false);
            }
            else
            {
                // Reserved instruction (00000000)
                throw new InvalidOperationException("Reserved instruction encountered.");
            }
        }
        result.Position = 0;
        return await result.ToArrayAsync(result.Length).ConfigureAwait(false);
    }

    private static async Task ReadCopyInstructionAsync(Stream stream, int instruction, byte[] baseObject, Stream result)
    {
        var offset = ReadCopyInstructionOffset(stream, instruction);
        var size = GetCopyInstructionLength(stream, instruction);
        await result.WriteAsync(baseObject.AsMemory((int)offset, size)).ConfigureAwait(false);
    }

    private static async Task ReadAddNewDataInstructionAsync(Stream stream, int instruction, PooledMemoryStream result)
    {
        var size = instruction & 0x7F;
        await result.WriteAsync(stream, size).ConfigureAwait(false);
    }

    private static long ReadCopyInstructionOffset(Stream stream, int instruction)
    {
        long offset = 0;
        int shift = 0;
        if ((instruction & 0x01) != 0)
            offset |= (long)stream.ReadNonEosByteOrThrow() << shift;
        shift += 8;
        if ((instruction & 0x02) != 0)
            offset |= (long)stream.ReadNonEosByteOrThrow() << shift;
        shift += 8;
        if ((instruction & 0x04) != 0)
            offset |= (long)stream.ReadNonEosByteOrThrow() << shift;
        shift += 8;
        if ((instruction & 0x08) != 0)
            offset |= (long)stream.ReadNonEosByteOrThrow() << shift;
        return offset;
    }

    private static int GetCopyInstructionLength(Stream stream, int instruction)
    {
        int size = 0;
        int shift = 0;
        if ((instruction & 0x10) != 0)
            size |= stream.ReadNonEosByteOrThrow() << shift;
        shift += 8;
        if ((instruction & 0x20) != 0)
            size |= stream.ReadNonEosByteOrThrow() << shift;
        shift += 8;
        if ((instruction & 0x40) != 0)
            size |= stream.ReadNonEosByteOrThrow() << shift;
        if (size == 0) size = 0x10000;
        return size;
    }

    internal static long ExtractOffset(Stream stream)
    {
        // offset encoding: n bytes with MSB set in all but the last one.
        // The offset is then the number constructed concatenating the lower 7 bit of each byte, and
        // for n >= 2 adding 2 ^ 7 + 2 ^ 14 + ... +2 ^ (7 * (n - 1)) to the result.
        var b = stream.ReadNonEosByteOrThrow() & 0xFF;
        long result = b & 0x7F;
        while ((b & 0x80) != 0)
        {
            result += 1;
            b = stream.ReadNonEosByteOrThrow() & 0xFF;
            result <<= 7;
            result += b & 0x7F;
        }
        return result;
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
                if (!_disposed.IsCancellationRequested) _disposed.Cancel();
                _disposed.Dispose();
                _offsetStreamReader.Dispose();
                logger?.LogDebug("PackReader disposed.");
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}