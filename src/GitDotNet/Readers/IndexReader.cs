using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using GitDotNet.Tools;

namespace GitDotNet.Readers;

internal delegate IndexReader IndexReaderFactory(string path, IObjectResolver objectResolver);

internal class IndexReader(int version, IObjectResolver objectResolver, IFileOffsetStreamReader offsetStreamReader) : IDisposable
{
    /// <summary>Gets the version of the index file.</summary>
    public int Version => version;

    internal static IndexReader Load(IFileOffsetStreamReader offsetStreamReader, IObjectResolver objectResolver)
    {
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            using var stream = offsetStreamReader.OpenRead(0L);
            stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
            var version = ReadVersion(stream, fourByteBuffer);

            return new IndexReader(version, objectResolver, offsetStreamReader);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fourByteBuffer);
        }
    }

    /// <summary>Gets the entries from the index file asynchronously.</summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="IndexEntry"/> instances.</returns>
    public async Task<IList<IndexEntry>> GetEntriesAsync()
    {
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        using var stream = offsetStreamReader.OpenRead(8);
        try
        {
            stream.Seek(8, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(fourByteBuffer.AsMemory(0, 4));
            var entryCount = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            var result = ImmutableList.CreateBuilder<IndexEntry>();
            for (int i = 0; i < entryCount; i++)
            {
                var entry = await ReadEntryAsync(stream, objectResolver, version, result.LastOrDefault());
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
        stream.Read(fourByteBuffer.AsSpan(0, 4));
        return BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
    }

    private static async Task<IndexEntry> ReadEntryAsync(Stream stream, IObjectResolver objectResolver, int version, IndexEntry? previousEntry)
    {
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var read = await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var cTime = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var cTimeNano = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var mTime = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var mTimeNano = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var dev = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var ino = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var mode = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var uid = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var gid = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 4));
            var fileSize = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

            var hash = new byte[20];
            read += await stream.ReadAsync(hash.AsMemory(0, 20));

            read += await stream.ReadAsync(fourByteBuffer.AsMemory(0, 2));
            var flags = BinaryPrimitives.ReadUInt16BigEndian(fourByteBuffer.AsSpan(0, 2));
            var isExtended = (flags & 0x4000) != 0;

            fourByteBuffer[0] = (byte)(fourByteBuffer[0] & 0xF);
            var length = BinaryPrimitives.ReadUInt16BigEndian(fourByteBuffer.AsSpan(0, 2));

            if (version >= 3 && isExtended)
            {
                read += await stream.ReadAsync(hash.AsMemory(0, 4));
            }

            var path = default(byte[]);
            if (version < 4)
            {
                path = new byte[length];
                read += await stream.ReadAsync(path.AsMemory(0, length));
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
        var epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Add the ctime seconds to the epoch
        var dateTimeOffset = epoch.AddSeconds(ctime);
        // Add the nanoseconds as ticks
        dateTimeOffset = dateTimeOffset.AddTicks(ctimeNano / 100);
        // Convert to DateTime
        return dateTimeOffset.UtcDateTime;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        offsetStreamReader.Dispose();
        GC.SuppressFinalize(this);
    }
}
