using GitDotNet.Tools;
using System.Collections.Concurrent;

namespace GitDotNet.Writers;

/// <summary>Provides delta compression utilities for pack files using rolling hash and match scoring.</summary>
internal static class DeltaCompression
{
    /// <summary>Default window size used for rolling hash calculations.</summary>
    internal const int DefaultWindowSize = 16; // Default window size for rolling hash
    private const int HashSize = 4096;
    private const int MinMatchLength = 4;
    private const int MaxMatchLength = 0xFFFF;
    /// <summary>Maximum length of data that can be encoded in a single literal instruction (7-bit limit).</summary>
    private const int MaxLiteralLength = 127;
    private const uint ModAdler = 65521; // Largest prime less than 65536
    private const int MaxPositionsPerHash = 16;

    /// <summary>Calculates the potential savings from creating a delta between target and source data.</summary>
    /// <param name="target">The target data to compress.</param>
    /// <param name="source">The source data to use as base.</param>
    /// <param name="sourceHashTable">Pre-computed hash table for the source data.</param>
    /// <param name="windowSize">The window size for rolling hash calculations.</param>
    /// <returns>The estimated savings in bytes.</returns>
    public static async Task<int> CalculateDeltaScoreAsync(byte[] target, byte[] source, Dictionary<uint, List<int>> sourceHashTable, int windowSize = DefaultWindowSize)
    {
        return await Task.Run(() =>
        {
            // If sizes are very different, skip expensive calculation
            var sizeRatio = (double)Math.Min(target.Length, source.Length) / Math.Max(target.Length, source.Length);
            if (sizeRatio < 0.3)
            {
                return 0;
            }

            var matches = FindMatches(target, source, sourceHashTable, windowSize);
            var totalMatchLength = matches.Sum(m => m.Length);

            var localityBonus = CalculateLocalityBonus(matches);
            totalMatchLength += localityBonus;

            // Apply a bonus for good size similarity
            if (sizeRatio > 0.8)
            {
                totalMatchLength = (int)(totalMatchLength * 1.05);
            }

            return totalMatchLength;
        }).ConfigureAwait(false);
    }

    /// <summary>Calculates a locality bonus based on how close matches are to each other.</summary>
    /// <param name="matches">The list of matches found.</param>
    /// <returns>A bonus score for good locality.</returns>
    private static int CalculateLocalityBonus(List<DeltaMatch> matches)
    {
        if (matches.Count < 2) return 0;

        var sortedMatches = matches.OrderBy(m => m.TargetOffset).ToList();
        var gapPenalty = 0;
        const int maxGapForBonus = 100; // Matches within 100 bytes get locality bonus

        for (int i = 1; i < sortedMatches.Count; i++)
        {
            var prevMatch = sortedMatches[i - 1];
            var currMatch = sortedMatches[i];

            var gap = currMatch.TargetOffset - (prevMatch.TargetOffset + prevMatch.Length);

            switch (gap)
            {
                case < maxGapForBonus and >= 0:
                    // Small gaps get a bonus (good locality)
                    gapPenalty += Math.Max(0, maxGapForBonus - gap) / 10;
                    break;
                case > maxGapForBonus:
                    // Large gaps get a penalty
                    gapPenalty -= gap / 20;
                    break;
            }
        }

        return Math.Max(0, gapPenalty);
    }

    /// <summary>Creates delta data from target using source as base.</summary>
    /// <param name="target">The target data to compress.</param>
    /// <param name="source">The source data to use as base.</param>
    /// <param name="sourceHashTable">Pre-computed hash table for the source data.</param>
    /// <param name="windowSize">The window size for rolling hash calculations.</param>
    /// <returns>The delta-compressed data.</returns>
    public static async Task<byte[]> CreateDeltaAsync(byte[] target, byte[] source, Dictionary<uint, List<int>> sourceHashTable, int windowSize = DefaultWindowSize)
    {
        return await Task.Run(() =>
        {
            using var deltaStream = new PooledMemoryStream();

            // Write source size (variable length encoding)
            WriteVarInt(deltaStream, (ulong)source.Length);

            // Write target size (variable length encoding)
            WriteVarInt(deltaStream, (ulong)target.Length);

            // Generate delta instructions with improved matching
            var matches = FindMatches(target, source, sourceHashTable, windowSize);
            var targetPos = 0;

            foreach (var match in matches.OrderBy(m => m.TargetOffset))
            {
                // Add any bytes before this match as literal data
                if (match.TargetOffset > targetPos)
                {
                    var literalLength = match.TargetOffset - targetPos;
                    WriteLiteralInstructions(deltaStream, target.AsSpan(targetPos, literalLength));
                }

                // Add copy instruction for the match
                WriteCopyInstruction(deltaStream, match.BaseOffset, match.Length);

                targetPos = match.TargetOffset + match.Length;
            }

            // Add any remaining bytes as literal data
            if (targetPos < target.Length)
            {
                var remainingLength = target.Length - targetPos;
                WriteLiteralInstructions(deltaStream, target.AsSpan(targetPos, remainingLength));
            }

            return ToArray(deltaStream);
        }).ConfigureAwait(false);
    }

    /// <summary>Pre-builds hash tables for all entries to avoid rebuilding them repeatedly.</summary>
    /// <param name="typeEntries">The list of entries to build hash tables for.</param>
    /// <param name="windowSize">The window size for rolling hash calculations.</param>
    /// <returns>A dictionary mapping entry IDs to their hash tables.</returns>
    public static Dictionary<HashId, Dictionary<uint, List<int>>> BuildEntryHashTables(List<PackEntry> typeEntries, int windowSize = DefaultWindowSize)
    {
        var result = new Dictionary<HashId, Dictionary<uint, List<int>>>();

        // Use parallel processing for building hash tables when we have many entries
        if (typeEntries.Count > 10)
        {
            var partitioner = Partitioner.Create(typeEntries, true);
            var lockObject = new object();

            Parallel.ForEach(partitioner, entry =>
            {
                if (!result.ContainsKey(entry.Id))
                {
                    var hashTable = BuildHashTable(entry.Data, windowSize);

                    lock (lockObject)
                    {
                        result.TryAdd(entry.Id, hashTable);
                    }
                }
            });
        }
        else
        {
            // Use sequential processing for small numbers of entries
            foreach (var entry in typeEntries.Where(entry => !result.ContainsKey(entry.Id)))
            {
                result[entry.Id] = BuildHashTable(entry.Data, windowSize);
            }
        }

        return result;
    }

    /// <summary>Builds a hash table for the given data for efficient delta matching.</summary>
    /// <param name="data">The data to build hash table for.</param>
    /// <param name="windowSize">The window size for rolling hash calculations.</param>
    /// <returns>A hash table mapping hash values to byte positions.</returns>
    public static Dictionary<uint, List<int>> BuildHashTable(byte[] data, int windowSize = DefaultWindowSize)
    {
        var hashTable = new Dictionary<uint, List<int>>();

        // Handle edge case where data is too small
        if (data.Length < MinMatchLength)
            return hashTable;

        // For small data, fall back to MinMatchLength-based hashing
        return data.Length < windowSize ?
            BuildHashTableSmall(data, hashTable) :
            BuildHashTableLarge(data, windowSize, hashTable);
    }

    private static Dictionary<uint, List<int>> BuildHashTableSmall(byte[] data, Dictionary<uint, List<int>> hashTable)
    {
        for (int i = 0; i <= data.Length - MinMatchLength; i++)
        {
            var simpleHash = ComputeAdler32Hash(data.AsSpan(i, MinMatchLength));

            if (!hashTable.TryGetValue(simpleHash, out var positions))
            {
                hashTable[simpleHash] = positions = new List<int>(4); // Pre-allocate small capacity
            }
            positions.Add(i);
        }
        return hashTable;
    }

    private static Dictionary<uint, List<int>> BuildHashTableLarge(byte[] data, int windowSize, Dictionary<uint, List<int>> hashTable)
    {
        // Use Adler32-inspired rolling hash for better distribution
        uint a = 1, b = 0;

        // Compute initial hash for the first window
        for (int i = 0; i < windowSize; i++)
        {
            a = (a + data[i]) % ModAdler;
            b = (b + a) % ModAdler;
        }

        var hash = (b << 16) | a;
        hash %= HashSize;

        // Add the first window position
        if (!hashTable.TryGetValue(hash, out var windowPositions))
        {
            hashTable[hash] = windowPositions = new List<int>(8); // Pre-allocate reasonable capacity
        }
        windowPositions.Add(0);

        // Use rolling hash for subsequent windows
        for (int i = 1; i <= data.Length - windowSize; i++)
        {
            // Rolling Adler32: remove the leftmost character and add the new rightmost character
            var oldChar = data[i - 1];
            var newChar = data[i + windowSize - 1];

            // Update Adler32 components
            a = (a - oldChar + newChar) % ModAdler;
            b = (b - (uint)windowSize * oldChar + a - 1) % ModAdler;

            hash = ((b << 16) | a) % HashSize;

            if (!hashTable.TryGetValue(hash, out windowPositions))
            {
                hashTable[hash] = windowPositions = new List<int>(8); // Pre-allocate reasonable capacity
            }
            windowPositions.Add(i);

            // Limit the number of positions per hash to avoid quadratic behavior with highly repetitive data
            if (windowPositions.Count > MaxPositionsPerHash)
            {
                // Keep only the most recent positions (better for locality)
                windowPositions.RemoveRange(0, windowPositions.Count - MaxPositionsPerHash / 2);
            }
        }

        return hashTable;
    }

    /// <summary>Computes an Adler32-inspired hash for better distribution than simple polynomial hash.</summary>
    private static uint ComputeAdler32Hash(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        foreach (var t in data)
        {
            a = (a + t) % ModAdler;
            b = (b + a) % ModAdler;
        }

        return ((b << 16) | a) % HashSize;
    }

    private static byte[] ToArray(PooledMemoryStream stream)
    {
        var span = stream.GetByteSpan();
        return span.ToArray();
    }

    private static List<DeltaMatch> FindMatches(byte[] target, byte[] source, Dictionary<uint, List<int>> sourceHashTable, int windowSize)
    {
        // Handle edge cases early
        if (target.Length < MinMatchLength || sourceHashTable.Count == 0)
            return [];

        // Choose strategy based on target size
        return target.Length < windowSize ?
            FindMatchesForSmallTarget(target, source, sourceHashTable) :
            FindMatchesForLargeTarget(target, source, sourceHashTable, windowSize);
    }

    private static List<DeltaMatch> FindMatchesForSmallTarget(byte[] target, byte[] source, Dictionary<uint, List<int>> sourceHashTable)
    {
        var matches = new List<DeltaMatch>();

        for (int targetPos = 0; targetPos <= target.Length - MinMatchLength; targetPos++)
        {
            var hash = ComputeAdler32Hash(target.AsSpan(targetPos, MinMatchLength));

            if (!sourceHashTable.TryGetValue(hash, out var sourcePositions))
            {
                continue;
            }

            var bestMatch = FindLongestMatchAtPosition(target, source, targetPos, sourcePositions);
            if (bestMatch.Length >= MinMatchLength)
            {
                matches.Add(bestMatch);
#pragma warning disable S127 // "for" loop stop conditions should be invariant
                targetPos += bestMatch.Length - 1; // Skip matched bytes to avoid overlaps
#pragma warning restore S127 // "for" loop stop conditions should be invariant
            }
        }

        RemoveOverlappingMatches(matches);
        return matches;
    }

    private static List<DeltaMatch> FindMatchesForLargeTarget(byte[] target, byte[] source,
        Dictionary<uint, List<int>> sourceHashTable, int windowSize)
    {
        var matches = new List<DeltaMatch>();

        // Initialize Adler32 for rolling hash
        uint a = 1, b = 0;

        // Compute initial hash
        for (int i = 0; i < windowSize; i++)
        {
            a = (a + target[i]) % ModAdler;
            b = (b + a) % ModAdler;
        }

        // Process first window
        var hash = ((b << 16) | a) % HashSize;
        var matchFound = TryFindAndAddMatch(target, source, sourceHashTable, hash, 0, matches);

        // Continue with rolling hash, skipping ahead when matches are found
        int targetPos = matchFound ? matches[^1].TargetOffset + matches[^1].Length : 1;

        while (targetPos <= target.Length - windowSize)
        {
            // Update rolling hash
            var oldChar = target[targetPos - 1];
            var newChar = target[targetPos + windowSize - 1];

            a = (a - oldChar + newChar) % ModAdler;
            b = (b - (uint)windowSize * oldChar + a - 1) % ModAdler;
            hash = ((b << 16) | a) % HashSize;

            // Try to find match at current position
            matchFound = TryFindAndAddMatch(target, source, sourceHashTable, hash, targetPos, matches);

            // Skip ahead if match found, otherwise advance by 1
            targetPos = matchFound ? matches[^1].TargetOffset + matches[^1].Length : targetPos + 1;
        }

        RemoveOverlappingMatches(matches);
        return matches;
    }

    /// <summary>Simplified match finding helper that tries to find and add a match at the given position.</summary>
    private static bool TryFindAndAddMatch(byte[] target, byte[] source, Dictionary<uint, List<int>> sourceHashTable,
        uint hash, int targetPos, List<DeltaMatch> matches)
    {
        if (!sourceHashTable.TryGetValue(hash, out var sourcePositions))
            return false;

        var bestMatch = FindLongestMatchAtPosition(target, source, targetPos, sourcePositions);

        if (bestMatch.Length is < MinMatchLength or > MaxMatchLength)
        {
            return false;
        }

        matches.Add(bestMatch);
        return true;

    }

    /// <summary>Finds the longest match at a specific target position from candidate source positions.</summary>
    private static DeltaMatch FindLongestMatchAtPosition(byte[] target, byte[] source, int targetPos, List<int> sourcePositions)
    {
        var bestMatch = new DeltaMatch(0, 0, 0, 0);

        // Limit source positions to check for performance (most recent are likely better)
        var positionsToCheck = Math.Min(sourcePositions.Count, MaxPositionsPerHash);

        for (int i = 0; i < positionsToCheck; i++)
        {
            var sourcePos = sourcePositions[i];
            var matchLength = CalculateMatchLength(target, source, targetPos, sourcePos);

            if (matchLength >= MinMatchLength && matchLength > bestMatch.Length)
            {
                bestMatch = new DeltaMatch(sourcePos, targetPos, matchLength, matchLength);

                // Early exit for very good matches
                if (matchLength > 100) // Good enough threshold
                {
                    break;
                }
            }
        }

        return bestMatch;
    }

    /// <summary>Calculates the length of matching bytes between target and source at given positions.</summary>
    private static int CalculateMatchLength(byte[] target, byte[] source, int targetPos, int sourcePos)
    {
        int length = 0;
        int maxLength = Math.Min(target.Length - targetPos, source.Length - sourcePos);
        maxLength = Math.Min(maxLength, MaxMatchLength);

        while (length < maxLength && target[targetPos + length] == source[sourcePos + length])
        {
            length++;
        }

        return length;
    }

    /// <summary>Removes overlapping matches, keeping the ones that provide better coverage.</summary>
    private static void RemoveOverlappingMatches(List<DeltaMatch> matches)
    {
        if (matches.Count <= 1) return;

        int writeIndex = 0;
        int lastTargetEnd = -1;

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];

            if (match.TargetOffset >= lastTargetEnd)
            {
                matches[writeIndex++] = match;
                lastTargetEnd = match.TargetOffset + match.Length;
            }
        }

        // Remove any leftover items
        if (writeIndex < matches.Count)
        {
            matches.RemoveRange(writeIndex, matches.Count - writeIndex);
        }
    }

    private static void WriteVarInt(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)(value & 0x7F));
    }

    private static void WriteLiteralInstruction(Stream stream, ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxLiteralLength)
            throw new ArgumentOutOfRangeException(nameof(data), $"Literal data length must be 1-{MaxLiteralLength} bytes");

        // Write instruction byte (0xxxxxxx format)
        stream.WriteByte((byte)data.Length);

        // Write literal data
        stream.Write(data);
    }

    /// <summary>Writes literal data by splitting it into chunks of MaxLiteralLength bytes or less.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="data">The data to write as literal instructions.</param>
    private static void WriteLiteralInstructions(Stream stream, ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunkSize = Math.Min(MaxLiteralLength, data.Length - offset);
            WriteLiteralInstruction(stream, data.Slice(offset, chunkSize));
            offset += chunkSize;
        }
    }

    private static void WriteCopyInstruction(Stream stream, int offset, int length)
    {
        if (length == 0x10000) length = 0; // Special case for 64KB

        byte instruction = 0x80; // Copy instruction starts with 1xxxxxxx
        var offsetBytes = new List<byte>();
        var lengthBytes = new List<byte>();

        // Encode offset (up to 4 bytes)
        for (int i = 0; i < 4; i++)
        {
            if ((offset & (0xFF << (i * 8))) != 0)
            {
                instruction |= (byte)(1 << i);
                offsetBytes.Add((byte)(offset >> (i * 8)));
            }
        }

        // Encode length (up to 3 bytes)
        for (int i = 0; i < 3; i++)
        {
            if ((length & (0xFF << (i * 8))) != 0)
            {
                instruction |= (byte)(1 << (4 + i));
                lengthBytes.Add((byte)(length >> (i * 8)));
            }
        }

        // Write instruction byte
        stream.WriteByte(instruction);

        // Write offset bytes
        foreach (var b in offsetBytes)
            stream.WriteByte(b);

        // Write length bytes
        foreach (var b in lengthBytes)
            stream.WriteByte(b);
    }
}