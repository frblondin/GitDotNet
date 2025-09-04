using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.Logging;
using static System.Buffers.Binary.BinaryPrimitives;

namespace GitDotNet.Readers;

internal delegate IndexReader IndexReaderFactory(string path, IObjectResolver objectResolver);

internal class IndexReader(string path, IObjectResolver objectResolver, IFileSystem fileSystem, ILogger<IndexReader>? logger = null)
{
    /// <summary>Gets the entries from the index file asynchronously.</summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="IndexEntry"/> instances.</returns>
    public async Task<IImmutableList<IndexEntry>> GetEntriesAsync()
    {
        logger?.LogInformation("Getting index entries from: {Path}", path);
        if (!fileSystem.File.Exists(path))
        {
            logger?.LogWarning("Index file does not exist: {Path}", path);
            return [];
        }

        var fourByteBuffer = new byte[4];
        using var stream = fileSystem.File.OpenReadAsynchronous(path);

        await stream.ReadExactlyAsync(fourByteBuffer).ConfigureAwait(false);
        var version = ReadVersion(stream, fourByteBuffer);

        stream.Seek(8L, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(fourByteBuffer).ConfigureAwait(false);
        var entryCount = ReadInt32BigEndian(fourByteBuffer);

        var result = ImmutableList.CreateBuilder<IndexEntry>();
        var previousPath = string.Empty; // Track previous path for version 4 compression
            
        for (int i = 0; i < entryCount; i++)
        {
            var (entry, entryPath) = await ReadEntryAsync(stream, objectResolver, version, previousPath).ConfigureAwait(false);
            result.Add(entry);
            previousPath = entryPath; // Update for next iteration
        }
        return result.ToImmutable();
    }

    private static int ReadVersion(Stream stream, byte[] fourByteBuffer)
    {
        stream.ReadExactly(fourByteBuffer);
        return ReadInt32BigEndian(fourByteBuffer);
    }

    private static async Task<(IndexEntry entry, string path)> ReadEntryAsync(Stream stream, IObjectResolver objectResolver, int version, string previousPath)
    {
        var fourByteBuffer = new byte[4];
        var read = 0;
        var cTime = await ReadNextEntryAsync(stream, fourByteBuffer, ReadInt32BigEndian).ConfigureAwait(false);
        var cTimeNano = await ReadNextEntryAsync(stream, fourByteBuffer, ReadInt32BigEndian).ConfigureAwait(false);
        var mTime = await ReadNextEntryAsync(stream, fourByteBuffer, ReadInt32BigEndian).ConfigureAwait(false);
        var mTimeNano = await ReadNextEntryAsync(stream, fourByteBuffer, ReadInt32BigEndian).ConfigureAwait(false);
        stream.Seek(8, SeekOrigin.Current); read += 8; // dev and ino
        var mode = await ReadNextEntryAsync(stream, fourByteBuffer, ReadInt32BigEndian).ConfigureAwait(false);
        stream.Seek(8, SeekOrigin.Current); read += 8; // uid and gid
        var fileSize = await ReadNextEntryAsync(stream, fourByteBuffer, ReadInt32BigEndian).ConfigureAwait(false);
        var hash = await ReadNextEntryAsync(stream, new byte[20], b => new HashId(b.ToArray())).ConfigureAwait(false);
        var flags = await ReadNextEntryAsync(stream, fourByteBuffer, ReadUInt16BigEndian, 2).ConfigureAwait(false);
        var isExtended = (flags & 0x4000) != 0;
        fourByteBuffer[0] = (byte)(fourByteBuffer[0] & 0xF);
        var length = ReadUInt16BigEndian(fourByteBuffer.AsSpan(0, 2));

        if (version >= 3 && isExtended)
        {
            stream.Seek(4, SeekOrigin.Current);
            read += 4;
        }

        string path;
        if (version < 4)
        {
            path = await ReadNextEntryAsync(stream, new byte[length], Encoding.UTF8.GetString).ConfigureAwait(false);

            // 1 - 8 nul bytes as necessary to pad the entry to a multiple of eight bytes while keeping the name NUL - terminated
            stream.Seek(8 - (read % 8), SeekOrigin.Current);
        }
        else
        {
            path = ReadVersion4Path(stream, previousPath, ref read);
        }

        var entry = new IndexEntry(ConvertCtimeToDateTime(cTime, cTimeNano), ConvertCtimeToDateTime(mTime, mTimeNano),
                    (IndexEntryType)(mode >> 12), mode & 0x1FF,
                    fileSize, hash, path, objectResolver);

        return (entry, path);

        async Task<TResult> ReadNextEntryAsync<TResult>(Stream stream, byte[] fourByteBuffer, Reader<TResult> reader, int length = -1)
        {
            if (length == -1) length = fourByteBuffer.Length;
            await stream.ReadExactlyAsync(fourByteBuffer.AsMemory(0, length)).ConfigureAwait(false);
            var value = reader(fourByteBuffer.AsSpan(0, length));
            read += length;
            return value;
        }
    }

    private static string ReadVersion4Path(Stream stream, string previousPath, ref int read)
    {
        // Read the variable width encoded integer N
        var stripCount = ReadVariableLengthInteger(stream, ref read);
        read += CalculateVariableLengthIntegerSize(stripCount);

        // Read the NUL-terminated string S
        var pathSuffix = ReadNulTerminatedString(stream, ref read);
        read += pathSuffix.Length + 1; // +1 for the NUL terminator

        // Construct the path by removing N bytes from the end of previousPath and appending S
        var previousPathPrefix = previousPath.Length > stripCount ? 
            previousPath[..^stripCount] : 
            string.Empty;
        
        return previousPathPrefix + pathSuffix;
    }

    private static int ReadVariableLengthInteger(Stream stream, ref int read)
    {
        var buffer = stream.ReadByte();
        read++;
        var b = buffer & 0xFF;
        
        var result = b & 0x7F;
        while ((b & 0x80) != 0)
        {
            result += 1;
            buffer = stream.ReadByte();
            read++;
            b = buffer & 0xFF;
            result <<= 7;
            result += b & 0x7F;
        }
        return result;
    }

    /// <summary>Calculates the number of bytes used to encode a variable-length integer.</summary>
    /// <param name="value">The integer value.</param>
    /// <returns>The number of bytes used in the encoding.</returns>
    private static int CalculateVariableLengthIntegerSize(int value)
    {
        if (value == 0) return 1;
        
        var count = 0;
        var temp = value;
        
        // Count the first 7 bits
        count++;
        temp >>= 7;
        
        // Count additional bytes
        while (temp > 0)
        {
            temp--;
            count++;
            temp >>= 7;
        }
        
        return count;
    }

    private static string ReadNulTerminatedString(Stream stream, ref int read)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = (byte)stream.ReadByte();
            read++;
            if (b == 0) break; // NUL terminator
            bytes.Add(b);
        }
        
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private delegate TResult Reader<out TResult>(ReadOnlySpan<byte> span);

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
