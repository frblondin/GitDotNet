using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;

namespace GitDotNet.Readers;

internal delegate Task<PackIndexReader> PackIndexFactory(string path);

internal class PackIndexReader(string path, int version, int[] fanout, byte[] packChecksum, byte[] checksum, IFileSystem fileSystem)
{
    private const int FanOutTableSize = 256;

    private readonly int[] _fanout = fanout;
    private readonly byte[] _data = fileSystem.File.ReadAllBytes(path);

    public int Version { get; } = version;
    public byte[] PackChecksum { get; } = packChecksum;
    public byte[] Checksum { get; } = checksum;
    public int Count => _fanout[FanOutTableSize - 1];

    private int SortedObjectNamesOffset => Version switch
    {
        2 => 2 * 4 + FanOutTableSize * 4,
        _ => throw new NotSupportedException($"Version {Version} is not supported.")
    };

    private int Crc32ValuesOffset => Version switch
    {
        2 => SortedObjectNamesOffset + Count * ObjectResolver.HashLength,
        _ => throw new NotSupportedException($"Version {Version} is not supported.")
    };

    private int PackFilePositionOffset => Version switch
    {
        2 => Crc32ValuesOffset + Count * 4,
        _ => throw new NotSupportedException($"Version {Version} is not supported.")
    };

    [ExcludeFromCodeCoverage]
    private int LargePackFilePositionOffset => Version switch
    {
        2 => PackFilePositionOffset + Count * (ObjectResolver.HashLength + 4),
        _ => throw new NotSupportedException($"Version {Version} is not supported.")
    };

    public static async Task<PackIndexReader> LoadAsync(string path, IFileSystem fileSystem)
    {
        if (!fileSystem.File.Exists(path))
        {
            throw new FileNotFoundException($"Pack index file not found: {path}");
        }
        await using var stream = fileSystem.File.OpenReadAsynchronous(path);
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            await stream.ReadExactlyAsync(fourByteBuffer.AsMemory(0, 4));
            var version = await ReadVersion(stream, fourByteBuffer);
            var fanout = new int[FanOutTableSize];
            for (var i = 0; i < FanOutTableSize; i++)
            {
                await stream.ReadExactlyAsync(fourByteBuffer.AsMemory(0, 4));
                fanout[i] = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
            }

            // Read last 20-byte of stream to get the last hash
            stream.Seek(-2 * ObjectResolver.HashLength, SeekOrigin.End);
            var packChecksum = new byte[ObjectResolver.HashLength];
            await stream.ReadExactlyAsync(packChecksum);
            var checksum = new byte[ObjectResolver.HashLength];
            await stream.ReadExactlyAsync(packChecksum);

            return new PackIndexReader(path, version, fanout, packChecksum, checksum, fileSystem);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fourByteBuffer);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Only for debugging purpose.")]
    public async Task<HashId> GetHashAsync(long offset)
    {
        for (int i = 0; i < Count; i++)
        {
            var packFileOffset = await GetPackFileOffsetAsync(i);
            if (packFileOffset == offset)
            {
                return await GetHashAsync(i);
            }
        }
        throw new NotSupportedException($"Couldn't find the corresponding hash for offset {offset} in pack index {path}.");
    }

    public async Task<HashId> GetHashAsync(int index)
    {
        using var stream = new MemoryStream(_data, writable: false);
        stream.Seek(SortedObjectNamesOffset + index * 20, SeekOrigin.Begin);
        var hash = new byte[ObjectResolver.HashLength];
        await stream.ReadExactlyAsync(hash.AsMemory(0, ObjectResolver.HashLength));
        return hash;
    }

    public async Task<int> GetIndexOfAsync(HashId id)
    {
        using var stream = new MemoryStream(_data, writable: false);
        var hashBuffer = new byte[ObjectResolver.HashLength];
        var fanoutPosition = id.Hash[0];
        var end = _fanout[fanoutPosition];
        var start = fanoutPosition > 0 ? _fanout[fanoutPosition - 1] : 0;

        stream.Seek(SortedObjectNamesOffset + start * 20, SeekOrigin.Begin);

        (end, start, var result) = await FindHashIndexAsync(id, stream, hashBuffer, end, start);
        if (result != -1 && id.Hash.Count < ObjectResolver.HashLength)
        {
            await CheckForAmbiguousHash(start, end, result, hashBuffer);
        }
        return result;

        async Task CheckForAmbiguousHash(int start, int end, int alreadyFound, byte[] hashBuffer)
        {
            stream.Seek(SortedObjectNamesOffset + start * 20, SeekOrigin.Begin);
            for (var i = start; i < end; i++)
            {
                await stream.ReadExactlyAsync(hashBuffer);
                CheckForHashCollision(id, alreadyFound, hashBuffer, i);
            }
        }
    }

    private async Task<(int end, int start, int result)> FindHashIndexAsync(HashId id, MemoryStream stream, byte[] hashBuffer, int end, int start)
    {
        var result = -1;
        while (start < end)
        {
            var mid = (start + end) / 2;
            stream.Seek(SortedObjectNamesOffset + mid * 20, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(hashBuffer.AsMemory(0, ObjectResolver.HashLength));

            var comparison = id.CompareTo(hashBuffer.AsSpan(0, id.Hash.Count));
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

        return (end, start, result);
    }

    [ExcludeFromCodeCoverage]
    private static void CheckForHashCollision(HashId id, int alreadyFound, byte[] hashBuffer, int i)
    {
        if (id.CompareTo(hashBuffer.AsSpan(0, id.Hash.Count)) == 0 && i != alreadyFound)
        {
            throw new AmbiguousHashException();
        }
    }

    public async Task<long> GetPackFileOffsetAsync(int index)
    {
        using var stream = new MemoryStream(_data, writable: false);
        var offset = ArrayPool<byte>.Shared.Rent(4);
        var hashBuffer = ArrayPool<byte>.Shared.Rent(ObjectResolver.HashLength);
        try
        {
            stream.Seek(PackFilePositionOffset + index * 4, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(offset.AsMemory(0, 4));
            var result = (long)BinaryPrimitives.ReadInt32BigEndian(offset.AsSpan(0, 4));
            result = await CalculateLargePackFileOffset(index, stream, offset, result);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashBuffer);
            ArrayPool<byte>.Shared.Return(offset);
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task<long> CalculateLargePackFileOffset(int index, MemoryStream stream, byte[] offset, long result)
    {
        if (result > 0x80000000)
        {
            stream.Seek(LargePackFilePositionOffset + index * 8, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(offset.AsMemory(0, 8));
            result = BinaryPrimitives.ReadInt32BigEndian(offset.AsSpan(0, 4));
        }

        return result;
    }

    private static async Task<int> ReadVersion(Stream stream, byte[] fourByteBuffer)
    {
        var version = 1;
        // Version is set only if bytes equal 255, 116, 79, 99
        if (fourByteBuffer[0] == 255 && fourByteBuffer[1] == 116 && fourByteBuffer[2] == 79 && fourByteBuffer[3] == 99)
        {
            await stream.ReadExactlyAsync(fourByteBuffer.AsMemory(0, 4));
            version = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
        }
        else
        {
            // In version 1 of pack files, the index does not have a header
            stream.Seek(0, SeekOrigin.Begin);
        }

        return version;
    }
}
