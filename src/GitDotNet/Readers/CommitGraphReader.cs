using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Text;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<CommitGraphReader>? _logger;
    private bool _disposedValue;

    public CommitGraphReader(string path,
        IObjectResolver objectResolver,
        IFileSystem fileSystem,
        FileOffsetStreamReaderFactory offsetStreamReaderFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _objectResolver = objectResolver;
        _logger = loggerFactory?.CreateLogger<CommitGraphReader>();
        var commitGraphPath = fileSystem.Path.Combine(path, "info", "commit-graph");
        var commitGraphChainPath = fileSystem.Path.Combine(path, "objects", "info", "commit-graphs", "commit-graph-chain");

        if (fileSystem.File.Exists(commitGraphChainPath))
        {
            try
            {
                _graphFiles = ReadCommitGraphChain(path, fileSystem, offsetStreamReaderFactory, commitGraphChainPath, loggerFactory);
            }
            catch (InvalidDataException ex)
            {
                _logger?.LogError(ex, "Invalid commit-graph chain file signature at {Path}", commitGraphChainPath);
                throw;
            }
        }
        else if (fileSystem.File.Exists(commitGraphPath))
        {
            var commitGraphReader = offsetStreamReaderFactory(commitGraphPath);
            _graphFiles = [new GraphFile(commitGraphReader, loggerFactory?.CreateLogger<GraphFile>())];
        }
        else
        {
            _logger?.LogWarning("No commit-graph file found at {Path}", path);
            _graphFiles = [];
        }
        _logger?.LogDebug("CommitGraphReader constructed with {Count} graph files", _graphFiles.Count);
    }

    public bool IsEmpty => _graphFiles.Count == 0;

    private int HashLength => _graphFiles.Count > 0 ? _graphFiles[0].HashLength : ObjectResolver.HashLength;

    [ExcludeFromCodeCoverage]
    private static List<GraphFile> ReadCommitGraphChain(string path, IFileSystem fileSystem,
        FileOffsetStreamReaderFactory offsetStreamReaderFactory, string commitGraphChainPath,
        ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger<CommitGraphReader>();
        logger?.LogDebug("ReadCommitGraphChain called for {ChainPath}", commitGraphChainPath);
        // Load multiple commit-graph files
        var result = new List<GraphFile>();
        var chainStream = fileSystem.File.OpenRead(commitGraphChainPath);
        var chainBuffer = new byte[8];
        chainStream.ReadExactly(chainBuffer.AsSpan(0, 8));
        var signature = Encoding.ASCII.GetString(chainBuffer.AsSpan(0, 8));
        if (signature != "CGC\x01\x00\x00\x00")
        {
            logger?.LogError("Invalid commit-graph chain file signature: {Signature}", signature);
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
                result.Add(new GraphFile(commitGraphReader, loggerFactory?.CreateLogger<GraphFile>()));
            }
            else
            {
                logger?.LogWarning("Commit-graph file {GraphPath} not found at {FullGraphPath}", graphPath, fullGraphPath);
            }
        }

        return result;
    }

    public LogEntry? Get(HashId commit)
    {
        _logger?.LogDebug("CommitGraphReader.Get called for commit {Commit}", commit);
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

    [ExcludeFromCodeCoverage]
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

    private LogEntry ParseCommitEntry(HashId commitHash, int commitIndex, GraphFile graph)
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
        ReadFirstParent(parent1Position, parents);
        ReadExtraParents(graph, parent2Position, parents);

        return new LogEntry(commitHash, treeId, parents.ToImmutable(),
            DateTimeOffset.FromUnixTimeSeconds(commitTime), _objectResolver);
    }

    private void ReadFirstParent(int parent1Position, ImmutableList<HashId>.Builder parents)
    {
        if (parent1Position != NoParent)
        {
            var parentId = GetCommitId(parent1Position);
            parents.Add(parentId);
        }
    }

    private void ReadExtraParents(GraphFile graph, int parent2Position, ImmutableList<HashId>.Builder parents)
    {
        if (parent2Position == NoParent) return;

        if (!ReadExtraEdgeListParents(parents, parent2Position, graph))
        {
            var parentId = GetCommitId(parent2Position);
            parents.Add(parentId);
        }
    }

    private bool ReadExtraEdgeListParents(ImmutableList<HashId>.Builder parents, int parent2Position, GraphFile graph)
    {
        const uint ExtraParents = 0x80000000;

        if ((parent2Position & ExtraParents) == 0) return false;

        var extraParentIndex = parent2Position & 0x7FFFFFFF;
        if (graph.ExtraEdgeListOffset == -1)
        {
            throw new InvalidOperationException("Extra Edge List chunk not found in commit-graph file.");
        }

        using var stream = graph.Reader.OpenRead(graph.ExtraEdgeListOffset + extraParentIndex * 4);
        var fourByteBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            while (true)
            {
                stream.ReadExactly(fourByteBuffer.AsSpan(0, 4));
                var parentIndex = BinaryPrimitives.ReadInt32BigEndian(fourByteBuffer.AsSpan(0, 4));
                var parentId = GetCommitId(parentIndex);
                parents.Add(parentId);

                if ((parentIndex & ExtraParents) != 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fourByteBuffer);
        }
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _logger?.LogDebug("CommitGraphReader disposing managed resources");
                DisposeGraphs();
                _logger?.LogInformation("CommitGraphReader disposed.");
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
