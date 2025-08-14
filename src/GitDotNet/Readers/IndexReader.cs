using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Text;

namespace GitDotNet.Readers;

internal delegate IndexReader IndexReaderFactory(string path, IObjectResolver objectResolver);

internal class IndexReader(string path, IObjectResolver objectResolver, IFileSystem fileSystem)
{
    /// <summary>Gets the entries from the index file asynchronously.</summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="IndexEntry"/> instances.</returns>
    public async Task<IImmutableList<IndexEntry>> GetEntriesAsync()
    {
        if (!fileSystem.File.Exists(path)) return [];

        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        using var stream = fileSystem.File.OpenReadAsynchronous(path);
        try
        {
            stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
            var version = ReadVersion(stream, fourByteBuffer);

            stream.Seek(8L, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var entryCount = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            var result = ImmutableList.CreateBuilder<IndexEntry>();
            for (int i = 0; i < entryCount; i++)
            {
                var entry = await ReadEntryAsync(stream, objectResolver, version).ConfigureAwait(false);
                result.Add(entry);
            }
            return result.ToImmutable();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fourByteBuffer);
        }
    }

    private static int ReadVersion(Stream stream, byte[] fourByteBuffer)
    {
        stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
        return BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
    }

    private static async Task<IndexEntry> ReadEntryAsync(Stream stream, IObjectResolver objectResolver, int version)
    {
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var read = await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var cTime = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var cTimeNano = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var mTime = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var mTimeNano = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var mode = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4)).ConfigureAwait(false);
            var fileSize = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            var hash = new byte[20];
            read += await stream.ReadAsync(hash.AsMemory(0, 20)).ConfigureAwait(false);

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 2)).ConfigureAwait(false);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(fourByteBuffer.AsSpan(0, 2));
            var isExtended = (flags & 0x4000) != 0;

            fourByteBuffer[0] = (byte)(fourByteBuffer[0] & 0xF);
            var length = BinaryPrimitives.ReadUInt16BigEndian(fourByteBuffer.AsSpan(0, 2));

            if (version >= 3 && isExtended)
            {
                read += await stream.ReadAsync(hash.AsMemory(0, 4)).ConfigureAwait(false);
            }

            var path = default(byte[]);
            if (version < 4)
            {
                path = new byte[length];
                read += await stream.ReadAsync(path.AsMemory(0, length)).ConfigureAwait(false);
            }
            else
            {
                // Version 4 path name handling
                throw new NotImplementedException();
            }

            // 1 - 8 nul bytes as necessary to pad the entry to a multiple of eight bytes while keeping the name NUL - terminated
            stream.Seek(8 - (read % 8), SeekOrigin.Current);

            return new(ConvertCtimeToDateTime(cTime, cTimeNano), ConvertCtimeToDateTime(mTime, mTimeNano),
                       (IndexEntryType)(mode >> 12), mode & 0x1FF,
                       fileSize, hash, Encoding.UTF8.GetString(path), objectResolver);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fourByteBuffer);
        }
    }

    private static DateTime ConvertCtimeToDateTime(int ctime, int ctimeNano)
    {
        // Unix epoch start time
        var epoch = DateTimeOffset.UnixEpoch;
        // Add the ctime seconds to the epoch
        var dateTimeOffset = epoch.AddSeconds(ctime);
        // Add the nanoseconds as ticks
        dateTimeOffset = dateTimeOffset.AddTicks(ctimeNano / 100);
        // Convert to DateTime
        return dateTimeOffset.UtcDateTime;
    }
}
