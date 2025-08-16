using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal partial class CommitGraphReader
{
    private sealed class GraphFile : IDisposable
    {
        private readonly ILogger<GraphFile>? _logger;

        public GraphFile(IFileOffsetStreamReader reader, ILogger<GraphFile>? logger = null)
        {
            Reader = reader;
            _logger = logger;
            _logger?.LogDebug("GraphFile initialized for reader path: {Path}", reader.Path);
            var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
            var eightByteBuffer = ArrayPool<byte>.Shared.Rent(8);
            try
            {
                using var stream = reader.OpenRead(0L);
                (HashLength, NumChunks) = ReadCommitGraphHeader(stream, fourByteBuffer);
                var chunkOffsets = ReadChunkOffsets(fourByteBuffer, eightByteBuffer, stream);

                // Locate the commit entry in the commit-graph file
                if (!chunkOffsets.TryGetValue(OidFanoutKey, out OidFanoutOffset) ||
                    !chunkOffsets.TryGetValue(OidLookupKey, out OidLookupOffset) ||
                    !chunkOffsets.TryGetValue(CommitDataKey, out var commitDataOffset))
                {
                    throw new InvalidDataException("Invalid commit-graph file format.");
                }
                CommitDataOffset = commitDataOffset;
                if (chunkOffsets.TryGetValue(ExtraEdgeListKey, out var extraEdgeListOffset))
                {
                    ExtraEdgeListOffset = extraEdgeListOffset;
                }
                else
                {
                    ExtraEdgeListOffset = -1L;
                }

                FanOutTable = ReadFanoutTable(fourByteBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fourByteBuffer);
                ArrayPool<byte>.Shared.Return(eightByteBuffer);
            }
        }

        public IFileOffsetStreamReader Reader { get; }

        public int HashLength { get; }

        public byte NumChunks { get; }
        public int[] FanOutTable { get; }
        public int CommitCount => FanOutTable[^1];

        public readonly long OidFanoutOffset;
        public readonly long OidLookupOffset;
        public long CommitDataOffset { get; }
        public long ExtraEdgeListOffset { get; }
        private bool _disposedValue;

        private static (int HashLength, byte NumChunk) ReadCommitGraphHeader(Stream stream, byte[] fourByteBuffer)
        {
            stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
            var signature = Encoding.ASCII.GetString(fourByteBuffer.AsSpan(0, 4));
            if (signature != "CGPH")
            {
                throw new InvalidDataException("Invalid commit-graph file signature.");
            }

            stream.ReadExactly(fourByteBuffer.AsSpan(0, 1));

            stream.ReadExactly(fourByteBuffer.AsSpan(0, 1));
            var hashVersion = fourByteBuffer[0];
            var hashLength = hashVersion switch
            {
                1 => 20,
                2 => 32,
                _ => throw new InvalidDataException("Invalid commit-graph hash version.")
            };

            stream.ReadExactly(fourByteBuffer.AsSpan(0, 1));
            var numChunks = fourByteBuffer[0];

            stream.ReadExactly(fourByteBuffer.AsSpan(0, 1));

            return (hashLength, numChunks);
        }

        private SortedDictionary<int, long> ReadChunkOffsets(byte[] fourByteBuffer, byte[] eightByteBuffer, Stream stream)
        {
            var chunkOffsets = new SortedDictionary<int, long>();
            for (int i = 0; i < NumChunks; i++)
            {
                stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
                var chunkId = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));

                stream.ReadExactly(eightByteBuffer.AsSpan(0, 8));
                var chunkOffset = BinaryPrimitives.ReadInt64BigEndian(eightByteBuffer.AsSpan(0, 8));

                chunkOffsets[chunkId] = chunkOffset;
            }
            return chunkOffsets;
        }

        private int[] ReadFanoutTable(byte[] fourByteBuffer)
        {
            using var stream = Reader.OpenRead(OidFanoutOffset);
            var fanoutTable = new int[256];
            for (int i = 0; i < 256; i++)
            {
                stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
                fanoutTable[i] = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
            }

            return fanoutTable;
        }

        internal int LocateCommitInLookupTable(HashId commitHash)
        {
            _logger?.LogDebug("Locating commit in lookup table: {CommitHash}", commitHash);
            var start = commitHash.Hash[0] == 0 ? 0 : FanOutTable[commitHash.Hash[0] - 1];
            var end = FanOutTable[commitHash.Hash[0]];
            var commitIndex = -1;

            // Calculate the total number of commits to read
            var totalCommits = end - start;
            if (totalCommits <= 0)
            {
                return commitIndex;
            }

            // Rent a buffer large enough to hold all the commit hashes
            var bufferSize = totalCommits * HashLength;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                using var stream = Reader.OpenRead(OidLookupOffset + start * HashLength);
                stream.ReadExactly(buffer.AsSpan(0, bufferSize));

                // Perform a binary search within the buffer
                int low = 0;
                int high = totalCommits - 1;

                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    var midSpan = buffer.AsSpan(mid * HashLength, HashLength);

                    int comparison = commitHash.CompareTo(midSpan);
                    if (comparison == 0)
                    {
                        commitIndex = start + mid;
                        break;
                    }
                    else if (comparison < 0)
                    {
                        high = mid - 1;
                    }
                    else
                    {
                        low = mid + 1;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return commitIndex;
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="GraphFile"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing"></param>
        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _logger?.LogDebug("Disposing GraphFile managed resources");
                    Reader.Dispose();
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
}