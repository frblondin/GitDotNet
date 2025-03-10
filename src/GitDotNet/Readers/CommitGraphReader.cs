using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Text;
using GitDotNet.Tools;

namespace GitDotNet.Readers;

internal delegate CommitGraphReader? CommitGraphReaderFactory(string path, IObjectResolver objectResolver);

internal partial class CommitGraphReader : IDisposable
{
    private const int OidFanoutKey = 0x4F494446;
    private const int OidLookupKey = 0x4F49444C;
    private const int CommitDataKey = 0x43444154;
    private const int ExtraEdgeListKey = 0x45444745;
    private const int NoParent = 0x70000000;

    private readonly IObjectResolver _objectResolver;
    private readonly IList<GraphFile> _graphFiles;
    private bool _disposedValue;

    private CommitGraphReader(IObjectResolver objectResolver,
                              IList<GraphFile> graphFiles)
    {
        _objectResolver = objectResolver;
        _graphFiles = graphFiles;
    }

    private int HashLength => _graphFiles[0].HashLength;

    public static CommitGraphReader? Load(string path,
                                          IObjectResolver objectResolver,
                                          IFileSystem fileSystem,
                                          FileOffsetStreamReaderFactory offsetStreamReaderFactory)
    {
        var commitGraphPath = fileSystem.Path.Combine(path, "info", "commit-graph");
        var commitGraphChainPath = fileSystem.Path.Combine(path, "objects", "info", "commit-graphs", "commit-graph-chain");

        IList<GraphFile> offsetStreamReaders;
        if (fileSystem.File.Exists(commitGraphChainPath))
        {
            offsetStreamReaders = ReadCommitGraphChain(path, fileSystem, offsetStreamReaderFactory, commitGraphChainPath);
        }
        else if (fileSystem.File.Exists(commitGraphPath))
        {
            var commitGraphReader = offsetStreamReaderFactory(commitGraphPath);
            offsetStreamReaders = [new GraphFile(commitGraphReader)];
        }
        else
        {
            return null;
        }

        return new CommitGraphReader(objectResolver, offsetStreamReaders);
    }

    [ExcludeFromCodeCoverage]
    private static List<GraphFile> ReadCommitGraphChain(string path, IFileSystem fileSystem, FileOffsetStreamReaderFactory offsetStreamReaderFactory, string commitGraphChainPath)
    {
        // Load multiple commit-graph files
        var result = new List<GraphFile>();
        var chainStream = fileSystem.File.OpenRead(commitGraphChainPath);
        var chainBuffer = new byte[8];
        chainStream.ReadExactly(chainBuffer.AsSpan(0, 8));
        var signature = Encoding.ASCII.GetString(chainBuffer.AsSpan(0, 8));
        if (signature != "CGC\x01\x00\x00\x00")
        {
            throw new InvalidDataException("Invalid commit-graph chain file signature.");
        }

        var graphCountBuffer = new byte[4];
        chainStream.ReadExactly(graphCountBuffer.AsSpan(0, 4));
        var graphCount = BinaryPrimitives.ReadInt32BigEndian(graphCountBuffer.AsSpan(0, 4));

        for (int i = 0; i < graphCount; i++)
        {
            var graphPathBuffer = new byte[256];
            chainStream.ReadExactly(graphPathBuffer.AsSpan(0, 256));
            var graphPath = Encoding.UTF8.GetString(graphPathBuffer).TrimEnd('\0');
            var fullGraphPath = Path.Combine(path, "objects", "info", "commit-graphs", graphPath);
            if (File.Exists(fullGraphPath))
            {
                var commitGraphReader = offsetStreamReaderFactory(fullGraphPath);
                result.Add(new GraphFile(commitGraphReader));
            }
        }

        return result;
    }

    public CommitEntry? Get(HashId commit)
    {
        foreach (var graph in _graphFiles)
        {
            var index = graph.LocateCommitInLookupTable(commit);
            if (index != -1)
            {
                return ParseCommitEntry(commit, index, graph);
            }
        }

        return null;
    }

    private HashId GetCommitId(int graphPosition)
    {
        var graph = FindGraphFile(graphPosition, out var lexPosition);
        var result = new byte[HashLength];
        using var stream = graph.Reader.OpenRead(graph.OidLookupOffset + lexPosition * HashLength);
        stream.ReadExactly(result);
        return result;
    }

    private GraphFile FindGraphFile(int graphPosition, out int lexPosition)
    {
        int cumulativeCount = 0;
        foreach (var graph in _graphFiles)
        {
            if (graphPosition < cumulativeCount + graph.CommitCount)
            {
                lexPosition = graphPosition - cumulativeCount;
                return graph;
            }
            cumulativeCount += graph.CommitCount;
        }
        throw new InvalidOperationException("Graph position out of range.");
    }

    private CommitEntry ParseCommitEntry(HashId commitHash,
                                         int commitIndex,
                                         GraphFile graph)
    {
        using var stream = graph.Reader.OpenRead(graph.CommitDataOffset + commitIndex * (HashLength + 16));

        // Read the OID of the root tree
        var treeId = new byte[HashLength];
        stream.ReadExactly(treeId);

        // Read the positions of the first two parents
        var eightByteBuffer = new byte[8];
        stream.ReadExactly(eightByteBuffer.AsSpan(0, 8));
        var parent1Position = BinaryPrimitives.ReadInt32BigEndian(eightByteBuffer.AsSpan(0, 4));
        var parent2Position = BinaryPrimitives.ReadInt32BigEndian(eightByteBuffer.AsSpan(4, 4));

        // Read the topological level and commit time
        stream.ReadExactly(eightByteBuffer.AsSpan(0, 8));
        var generationNumber = BinaryPrimitives.ReadInt32BigEndian(eightByteBuffer.AsSpan(0, 4));

        eightByteBuffer[0] = 0;
        eightByteBuffer[1] = 0;
        var commitTime = (long)BinaryPrimitives.ReadInt32BigEndian(eightByteBuffer.AsSpan(4, 4));
        // Extract the lowest 2 bits of previous 4 bytes for the commit time and add the high bits (33rs & 34th)
        commitTime |= (long)(generationNumber & 0x3) << 30;

        // Handle parent IDs
        var parents = ImmutableList.CreateBuilder<HashId>();
        const uint ExtraParents = 0x80000000;
        if (parent1Position != NoParent)
        {
            var parentId = GetCommitId(parent1Position);
            parents.Add(parentId);
        }

        if (parent2Position != NoParent)
        {
            if ((parent2Position & ExtraParents) != 0)
            {
                // Handle extra parents from the Extra Edge List chunk
                var extraParentIndex = parent2Position & 0x7FFFFFFF;
                ReadExtraParents(parents, extraParentIndex, graph);
            }
            else
            {
                var parentId = GetCommitId(parent2Position);
                parents.Add(parentId);
            }
        }

        return new CommitEntry(commitHash,
                               new Lazy<byte[]>(() => _objectResolver.GetDataAsync(commitHash).ConfigureAwait(false).GetAwaiter().GetResult()),
                               _objectResolver,
                               treeId,
                               parents.ToImmutable(),
                               DateTimeOffset.FromUnixTimeSeconds(commitTime));
    }

    [ExcludeFromCodeCoverage]
    private void ReadExtraParents(ImmutableList<HashId>.Builder parents, int extraParentIndex, GraphFile graph)
    {
        if (graph.ExtraEdgeListOffset == -1)
        {
            throw new InvalidOperationException("Extra Edge List chunk not found in commit-graph file.");
        }

        using var stream = graph.Reader.OpenRead(graph.CommitDataOffset + extraParentIndex * 4);
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            while (true)
            {
                stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
                var parentIndex = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
                if (parentIndex == NoParent)
                {
                    break;
                }

                var parentId = GetCommitId(parentIndex);
                parents.Add(parentId);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fourByteBuffer);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                DisposeGraphs();
            }

            _disposedValue = true;
        }
    }

    private void DisposeGraphs()
    {
        foreach (var graph in _graphFiles)
        {
            graph.Dispose();
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
