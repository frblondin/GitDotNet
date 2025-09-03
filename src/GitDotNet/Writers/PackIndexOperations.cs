namespace GitDotNet.Writers;

/// <summary>Handles writing pack index files.</summary>
/// <remarks>
/// This class has been optimized to support streaming CRC32 values directly from pack writing
/// to index writing, eliminating the need to store large Dictionary&lt;HashId, byte[]&gt; collections
/// in memory. The optimized approach is used internally by PackWriter.
/// </remarks>
internal static class PackIndexOperations
{
    /// <summary>Writes the index file header.</summary>
    /// <param name="stream">The stream to write to.</param>
    internal static async Task WriteIndexHeaderAsync(Stream stream)
    {
        // Write magic bytes for version 2: \xFF tOc
        var magicBytes = new byte[] { 0xFF, 0x74, 0x4F, 0x63 };
        await stream.WriteAsync(magicBytes).ConfigureAwait(false);
        
        // Write version (2)
        await PackFileOperations.WriteBigEndianIntAsync(stream, 2).ConfigureAwait(false);
    }

    /// <summary>Writes the fanout table for the index file.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="sortedEntries">The sorted list of pack entries.</param>
    internal static async Task WriteFanoutTableAsync(Stream stream, List<PackEntry> sortedEntries)
    {
        var fanout = BuildFanoutTable(sortedEntries);
        await WriteFanoutDataAsync(stream, fanout).ConfigureAwait(false);
    }

    /// <summary>Builds the fanout table for efficient hash lookups in the index.</summary>
    /// <param name="sortedEntries">The sorted list of pack entries.</param>
    /// <returns>An array representing the fanout table.</returns>
    private static int[] BuildFanoutTable(List<PackEntry> sortedEntries)
    {
        var fanout = new int[256];
        
        // Build fanout table - each entry contains the cumulative count of objects
        // whose first byte is <= that index
        int currentIndex = 0;
        for (int i = 0; i < 256; i++)
        {
            // Count objects whose first byte is exactly i
            while (currentIndex < sortedEntries.Count && 
                   sortedEntries[currentIndex].Id.Hash[0] == i)
            {
                currentIndex++;
            }
            
            // Set cumulative count for this and all remaining entries
            fanout[i] = currentIndex;
        }
        
        return fanout;
    }

    /// <summary>Writes the fanout table data to the stream.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="fanout">The fanout table to write.</param>
    private static async Task WriteFanoutDataAsync(Stream stream, int[] fanout)
    {
        foreach (var count in fanout)
        {
            await PackFileOperations.WriteBigEndianIntAsync(stream, count).ConfigureAwait(false);
        }
    }
}