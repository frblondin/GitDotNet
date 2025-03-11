using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.IO.Compression;

namespace GitDotNet.Readers;

internal delegate PackReader PackReaderFactory(string path);

internal class PackReader(IFileOffsetStreamReader offsetStreamReader, IOptions<GitConnection.Options> options, PackIndexFactory indexFactory, IMemoryCache memoryCache) : IDisposable
{
    private PackIndexReader? _index;
    private readonly CancellationTokenSource _disposed = new();

    public async Task<int> IndexOfAsync(HashId id)
    {
        var indexReader = await EnsureIndex();
        return await indexReader.GetIndexOfAsync(id);
    }

    private async Task<PackIndexReader> EnsureIndex() =>
        _index ??= await indexFactory(Path.ChangeExtension(offsetStreamReader.Path, "idx"));

    public async Task<UnlinkedEntry> GetAsync(int indexPosition, HashId id, Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        var index = await EnsureIndex();
        var offset = await index.GetPackFileOffsetAsync(indexPosition);

        return await GetByOffsetAsync(offset, async () => await ReadAsync(id, offset, dependentEntryProvider));
    }

    private async Task<UnlinkedEntry> GetAsync(HashId id,
                                               long offset,
                                               Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider) =>
        await GetByOffsetAsync(offset, async () => await ReadAsync(id, offset, dependentEntryProvider));

    private async Task<UnlinkedEntry> GetByOffsetAsync(long offset, Func<Task<UnlinkedEntry>> provider) =>
        (await memoryCache.GetOrCreateAsync((offsetStreamReader.Path, offset), async entry =>
        {
            var result = await provider();
            options.Value.ApplyTo(entry, result, _disposed.Token);
            return result;
        }))!;

    private async Task<UnlinkedEntry> ReadAsync(HashId id,
                                                long offset,
                                                Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(PackReader));

        using var stream = offsetStreamReader.OpenRead(offset);
        return await ReadAsync(stream, id, offset, dependentEntryProvider);
    }

    private async Task<UnlinkedEntry> ReadAsync(Stream stream,
                                                HashId id,
                                                long offset,
                                                Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
#if FILL_OFFSET_HASH_WHEN_MISSING
        if ((hash?.Length ?? 0) == 0)
        {
            var indexReader = await EnsureIndex();
            hash = await indexReader.GetHashAsync(offset);
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
                new(type, id, await ReadDataAsync(stream, length)),
            EntryType.RefDelta =>
                // Lazy load is not possible since we need to read base objects to get type
                await ReconstructRefDeltaAsync(stream, dependentEntryProvider),
            EntryType.OfsDelta =>
                // Lazy load is not possible since we need to read base objects to get type
                await ReconstructOfsDeltaAsync(stream, id, offset, dependentEntryProvider),
            _ => throw new NotImplementedException($"Unknown type {(int)type} while getting object."),
        };
        static async Task<byte[]> ReadDataAsync(Stream stream, long length)
        {
            var result = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            return await result.ToArrayAsync(length);
        }
    }

    private async Task<UnlinkedEntry> ReconstructOfsDeltaAsync(Stream stream,
                                                               HashId id,
                                                               long offset,
                                                               Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        var baseObjectOffset = ExtractOffset(stream);
        var baseObject = await GetAsync(HashId.Empty, offset - baseObjectOffset, dependentEntryProvider);

        var data = await ReconstructDeltaAsync(stream, baseObject.Data);
        return new(baseObject.Type, id, data);
    }

    private static async Task<UnlinkedEntry> ReconstructRefDeltaAsync(Stream stream, Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        var hash = new byte[20];
        if (stream.Read(hash) != hash.Length)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading ref delta hash.");
        }
        var baseObject = await dependentEntryProvider(hash);

        var data = await ReconstructDeltaAsync(stream, baseObject.Data);
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
                await ReadCopyInstructionAsync(decompressed, instruction, baseObject, result);
            }
            else if (instruction != 0)
            {
                // It's an add new data instruction (0xxxxxxx)
                await ReadAddNewDataInstructionAsync(decompressed, instruction, result);
            }
            else
            {
                // Reserved instruction (00000000)
                throw new InvalidOperationException("Reserved instruction encountered.");
            }
        }
        result.Position = 0;
        return await result.ToArrayAsync(result.Length);
    }

    private static async Task ReadCopyInstructionAsync(Stream stream, int instruction, byte[] baseObject, Stream result)
    {
        var offset = ReadCopyInstructionOffset(stream, instruction);
        var size = GetCopyInstructionLength(stream, instruction);
        await result.WriteAsync(baseObject.AsMemory((int)offset, size));
    }

    private static long ReadCopyInstructionOffset(Stream stream, int instruction)
    {
        var result = 0L;
        for (var i = 0; i < 4; i++)
        {
            if ((instruction & 1 << i) != 0)
            {
                var byteValue = stream.ReadNonEosByteOrThrow();
                result |= (long)byteValue << i * 8;
            }
        }

        return result;
    }

    private static int GetCopyInstructionLength(Stream stream, int instruction)
    {
        var result = 0;
        for (var i = 4; i < 7; i++)
        {
            if ((instruction & 1 << i) != 0)
            {
                var byteValue = stream.ReadNonEosByteOrThrow() & 0xFF;
                result |= byteValue << (i - 4) * 8;
            }
        }

        // Handle size zero case
        if (result == 0) result = 0x10000;

        return result;
    }

    private static async Task ReadAddNewDataInstructionAsync(Stream stream, int instruction, PooledMemoryStream result)
    {
        var size = instruction & 0x7F;
        await result.WriteAsync(stream, size);
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

    public async Task<int> GetCountAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(PackReader));

        return (await EnsureIndex()).Count;
    }

    internal async IAsyncEnumerable<(int Position, HashId Id)> GetHashesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(PackReader));

        var index = await EnsureIndex();
        for (int i = 0; i < index.Count; i++)
        {
            yield return (i, await index.GetHashAsync(i));
        }
    }

    internal async IAsyncEnumerable<UnlinkedEntry> GetEntriesAsync(Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, nameof(PackReader));

        var index = await EnsureIndex();
        await foreach (var (position, hash) in GetHashesAsync())
        {
            var offset = await index.GetPackFileOffsetAsync(position);
            yield return await GetAsync(hash, offset, dependentEntryProvider);
        }
    }

    public void Dispose()
    {
        if (!_disposed.IsCancellationRequested) _disposed.Cancel();
        _disposed.Dispose();
        offsetStreamReader.Dispose();
        GC.SuppressFinalize(this);
    }
}