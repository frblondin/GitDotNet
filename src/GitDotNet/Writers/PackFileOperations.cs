using System.IO.Compression;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;

/// <summary>Handles writing pack file entries and headers.</summary>
internal static class PackFileOperations
{
    private static readonly byte[] _packSignatureBytes = [0x50, 0x41, 0x43, 0x4B]; // "PACK" in ASCII
    private const int PackVersion = 2;

    /// <summary>Writes the pack header to the stream.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="entryCount">The number of entries in the pack.</param>
    /// <param name="logger">Optional logger for debugging.</param>
    public static async Task WritePackHeaderAsync(Stream stream, int entryCount, ILogger<PackWriter>? logger = null)
    {
        // Write "PACK" signature (4 bytes) - must be exact ASCII bytes
        await stream.WriteAsync(_packSignatureBytes).ConfigureAwait(false);

        // Write version (4 bytes, big-endian)
        await WriteBigEndianIntAsync(stream, PackVersion).ConfigureAwait(false);

        // Write number of objects (4 bytes, big-endian)
        await WriteBigEndianIntAsync(stream, entryCount).ConfigureAwait(false);

        logger?.LogDebug("Pack header written: version={Version}, objects={ObjectCount}", PackVersion, entryCount);
    }

    /// <summary>Writes a pack entry to the stream and computes CRC32 incrementally.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="entry">The entry to write.</param>
    /// <param name="objectOffsets">Dictionary to store object offsets for index creation.</param>
    /// <param name="logger">Optional logger for debugging.</param>
    /// <returns>The CRC32 hash of the entry data.</returns>
    /// <remarks>
    /// This method is optimized to compute CRC32 incrementally while writing data to the stream,
    /// eliminating the need for creating temporary memory streams and reducing memory allocation overhead.
    /// It uses a custom Crc32TrackingStream that computes the hash as data flows through it.
    /// </remarks>
    public static async Task<byte[]> WritePackEntryWithCrc32Async(Stream stream,
        PackEntry entry, Dictionary<HashId, int> objectOffsets, ILogger<PackWriter>? logger = null)
    {
        var startOffset = (int)stream.Position;
        objectOffsets[entry.Id] = startOffset;

        // Create a custom stream that tracks CRC32 while writing
        await using var crc32TrackingStream = new Crc32TrackingStream(stream);

        // Write type and length header
        WriteTypeAndLength(crc32TrackingStream, entry.Type, entry.Data.Length);

        switch (entry.Type)
        {
            // Write delta base information based on entry type
            case EntryType.RefDelta when entry.BaseId != null:
            {
                // Write base object hash for RefDelta entries
                var baseIdBytes = entry.BaseId.Hash.ToArray();
                await crc32TrackingStream.WriteAsync(baseIdBytes).ConfigureAwait(false);
                break;
            }
            case EntryType.OfsDelta when entry.BaseId != null:
            {
                // Write relative offset for OfsDelta entries
                if (!objectOffsets.TryGetValue(entry.BaseId, out var baseOffset))
                    throw new InvalidOperationException($"Base object {entry.BaseId} not found in offset map for OfsDelta entry {entry.Id}");

                var relativeOffset = startOffset - baseOffset;
                if (relativeOffset <= 0)
                    throw new InvalidOperationException($"Invalid relative offset {relativeOffset} for OfsDelta entry {entry.Id}");

                await WriteVariableLengthOffsetAsync(crc32TrackingStream, relativeOffset).ConfigureAwait(false);
                break;
            }
        }

        // Compress and write data
        using var compressedData = new MemoryStream();
        await using (var deflateStream = new ZLibStream(compressedData, CompressionMode.Compress, leaveOpen: true))
        {
            await deflateStream.WriteAsync(entry.Data).ConfigureAwait(false);
        }

        compressedData.Position = 0;
        await compressedData.CopyToAsync(crc32TrackingStream).ConfigureAwait(false);

        logger?.LogDebug("Wrote pack entry: id={Id}, type={Type}, offset={Offset}, compressed={CompressedSize}",
            entry.Id, entry.Type, startOffset, compressedData.Length);

        return crc32TrackingStream.GetCrc32Hash();
    }

    /// <summary>Writes a 32-bit integer in big-endian byte order to the stream.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="value">The integer value to write.</param>
    public static async Task WriteBigEndianIntAsync(Stream stream, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Writes a relative offset for OfsDelta entries using Git's variable-length encoding.</summary>
    internal static async Task WriteVariableLengthOffsetAsync(Stream stream, int offset)
    {
        if (offset <= 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be positive");

        // Based on Git's pack-objects.c write_offset() function
        // The encoding stores the offset in a variable-length format similar to LEB128
        // but with a special twist for multi-byte values

        var bytes = new List<byte>();
        uint c = (uint)offset;

        // First, extract the lowest 7 bits
        bytes.Add((byte)(c & 0x7F));
        c >>= 7;

        // For additional bytes, Git uses a special encoding
        while (c > 0)
        {
            // The key insight: Git decrements before masking for continuation bytes
            c--;
            bytes.Add((byte)(0x80 | (c & 0x7F)));
            c >>= 7;
        }

        // Reverse bytes for most significant first order
        bytes.Reverse();
        await stream.WriteAsync(bytes.ToArray()).ConfigureAwait(false);
    }

    /// <summary>Creates the type and length header bytes.</summary>
    private static void WriteTypeAndLength(Stream stream, EntryType type, int length)
    {
        int typeValue = (int)type;

        // First byte: TTTsssss (3 type bits, 1 continuation bit, 4 size bits)
        // Type is encoded in bits 6,5,4 (positions 2,1,0 counting from MSB)
        int firstByte = (typeValue << 4) | (length & 0x0F);
        length >>= 4;

        if (length > 0)
        {
            firstByte |= 0x80; // Set continuation bit (MSB)
        }

        stream.WriteByte((byte)firstByte);

        // Additional size bytes if needed - follow Git's variable-length encoding
        while (length > 0)
        {
            int sizeByte = length & 0x7F; // Take 7 bits
            length >>= 7;

            if (length > 0)
            {
                sizeByte |= 0x80; // Set continuation bit
            }

            stream.WriteByte((byte)sizeByte);
        }
    }
}