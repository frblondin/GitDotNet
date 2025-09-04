using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO.Abstractions;

namespace GitDotNet.Writers;

/// <summary>A delegate for creating PackWriter instances.</summary>
internal delegate PackWriter PackWriterFactory(IRepositoryInfo info);

/// <summary>Writes Git pack files in version 2 format with delta compression using rolling hash and match scoring.</summary>
/// <remarks>
/// <para>
/// This class implements an optimized approach for writing Git pack and index files simultaneously,
/// eliminating the need to store CRC32 values in memory through a Dictionary&lt;HashId, byte[]&gt;.
/// </para>
/// <para>
/// <strong>Optimization Details:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>The CryptoStream for index writing is moved up to the PackWriter level</description></item>
/// <item><description>Pack entries and index data are written simultaneously in sorted order</description></item>
/// <item><description>CRC32 values are computed and immediately written to the index stream</description></item>
/// <item><description>No intermediate Dictionary storage is required, reducing memory pressure significantly</description></item>
/// <item><description>Memory usage remains constant regardless of pack file size</description></item>
/// </list>
/// <para>
/// This approach provides substantial memory savings for large pack files while maintaining
/// full compatibility with the Git pack format specification.
/// </para>
/// </remarks>
internal class PackWriter : IDisposable
{
    private readonly IRepositoryInfo _info;
    private readonly string _temporaryPackPath;
    private readonly string _temporaryIndexPath;
    private readonly IFileSystem _fileSystem;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<PackWriter>? _logger;
    private readonly List<PackEntry> _entries = [];
    private readonly Dictionary<HashId, int> _objectOffsets = [];
    private readonly ConcurrentDictionary<HashId, PackEntry> _entryCache = [];
    private byte[]? _packChecksum;
    private bool _disposedValue;

    // Temporary streams for index writing optimization
    private CryptoStream? _indexCryptoStream;
    private SHA1? _indexSha1;
    private Stream? _indexBaseStream;

    /// <summary>Initializes a new instance of the PackWriter class.</summary>
    /// <param name="info">The info to the Git repository (typically the .git directory).</param>
    /// <param name="fileSystem">The file system abstraction.</param>
    /// <param name="loggerFactory">Optional logger factory for debugging and information.</param>
    public PackWriter(IRepositoryInfo info, IFileSystem fileSystem, ILoggerFactory? loggerFactory = null)
    {
        _info = info;
        _fileSystem = fileSystem;
        _loggerFactory = loggerFactory;
        _logger = _loggerFactory?.CreateLogger<PackWriter>();

        // Create a unique temporary pack filename using process ID and random component
        var processId = Environment.ProcessId;
        var randomSuffix = fileSystem.Path.GetRandomFileName().Replace(".", "")[..8]; // Take first 8 chars
        var tempPackFileName = $".tmp-{processId}-pack-{randomSuffix}.pack";
        var tempIndexFileName = $".tmp-{processId}-pack-{randomSuffix}.idx";

        // Ensure the objects/pack directory exists
        var objectsPackDir = fileSystem.Path.Combine(info.Path, "objects", "pack");
        fileSystem.Directory.CreateDirectory(objectsPackDir);

        _temporaryPackPath = fileSystem.Path.Combine(objectsPackDir, tempPackFileName);
        _temporaryIndexPath = fileSystem.Path.Combine(objectsPackDir, tempIndexFileName);

        _logger?.LogInformation("PackWriter initialized with temporary path: {Path}", _temporaryPackPath);
    }

    /// <summary>Gets the final pack file path after the pack has been written and disposed.</summary>
    /// <remarks>Returns null if the pack hasn't been written successfully yet.</remarks>
    public string? FinalPackPath => _packChecksum != null ?
        _fileSystem.Path.Combine(_info.Path, "objects", "pack", $"pack-{Convert.ToHexString(_packChecksum).ToLowerInvariant()}.pack") :
        null;

    /// <summary>Gets the final index file path after the pack has been written and disposed.</summary>
    /// <remarks>Returns null if the pack hasn't been written successfully yet.</remarks>
    public string? FinalIndexPath => _packChecksum != null ?
        _fileSystem.Path.Combine(_info.Path, "objects", "pack", $"pack-{Convert.ToHexString(_packChecksum).ToLowerInvariant()}.idx") :
        null;

    /// <summary>Adds an entry to the pack.</summary>
    /// <param name="type">The Git object type.</param>
    /// <param name="id">The object's hash ID.</param>
    /// <param name="data">The object's content data.</param>
    public void AddEntry(EntryType type, HashId id, byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, nameof(PackWriter));

        var entry = new PackEntry(type, id, data);
        _entries.Add(entry);
        _entryCache[id] = entry;

        _logger?.LogDebug("Added entry: type={Type}, id={Id}, size={Size}", type, id, data.Length);
    }

    /// <summary>Attempts to add an entry to the pack if it doesn't already exist.</summary>
    /// <param name="type">The Git object type.</param>
    /// <param name="id">The object's hash ID.</param>
    /// <param name="data">The object's content data.</param>
    /// <returns>True if the entry was added, false if it already existed.</returns>
    public bool TryAddEntry(EntryType type, HashId id, byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, nameof(PackWriter));

        if (_entryCache.ContainsKey(id))
        {
            _logger?.LogDebug("Entry already exists: type={Type}, id={Id}", type, id);
            return false;
        }

        AddEntry(type, id, data);
        return true;
    }

    /// <summary>Writes the pack file with enhanced delta compression using previous tree context and simultaneously streams the index file.</summary>
    /// <param name="previousRootTree">Optional previous root tree to check for similar objects by path.</param>
    /// <param name="entryPaths">Optional dictionary mapping entry IDs to their paths for previous tree lookup.</param>
    /// <param name="maxDeltaDepth">Maximum depth for delta chains.</param>
    /// <param name="windowSize">The window size for rolling hash calculations.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task WritePackAsync(TreeEntry? previousRootTree, Dictionary<HashId, GitPath> entryPaths,
        int maxDeltaDepth = PackOptimization.DefaultMaxDeltaDepth,
        int windowSize = DeltaCompression.DefaultWindowSize, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, nameof(PackWriter));

        _logger?.LogInformation("Writing pack file with {Count} entries", _entries.Count);

        // Use optimized approach that streams index data directly without storing all CRC32 values
        await WritePackAndIndexSimultaneouslyAsync(previousRootTree, entryPaths, maxDeltaDepth, windowSize, cancellationToken).ConfigureAwait(false);

        // Now compute SHA-1 checksum by reading the written file
        _packChecksum = await ComputePackChecksumAsync(_temporaryPackPath, cancellationToken).ConfigureAwait(false);

        await WritePackChecksumAsync(cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation("Pack file written successfully. Checksum: {Checksum}", Convert.ToHexString(_packChecksum));

        // Finalize the index file with the pack checksum
        await FinalizeIndexFileAsync(_packChecksum, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes pack and index files simultaneously in sorted order, eliminating the need for CRC32 storage.</summary>
    private async Task WritePackAndIndexSimultaneouslyAsync(TreeEntry? previousRootTree,
        Dictionary<HashId, GitPath> entryPaths, int maxDeltaDepth, int windowSize, CancellationToken cancellationToken)
    {
        // Create optimizer instance with context
        var optimizer = new PackOptimization(previousRootTree, entryPaths, maxDeltaDepth, windowSize, _loggerFactory?.CreateLogger<PackOptimization>());
        var optimizedEntries = await optimizer.OptimizeEntriesForDeltaCompressionAsync(_entries, cancellationToken).ConfigureAwait(false);

        // Sort entries in dependency order for pack writing (bases before deltas)
        // but keep index entries sorted by HashId as required by Git's index format
        var packSortedEntries = SortEntriesForDeltaChains(optimizedEntries);
        var indexSortedEntries = optimizedEntries.OrderBy(e => e.Id).ToList();

        await using var packStream = _fileSystem.File.Create(_temporaryPackPath);
        _indexBaseStream = _fileSystem.File.Create(_temporaryIndexPath);
        _indexSha1 = SHA1.Create();
        _indexCryptoStream = new CryptoStream(_indexBaseStream, _indexSha1, CryptoStreamMode.Write);

        // Write pack header
        await PackFileOperations.WritePackHeaderAsync(packStream, packSortedEntries.Count, _logger).ConfigureAwait(false);

        // Write index header and fanout table - must use HashId sorted order
        await PackIndexOperations.WriteIndexHeaderAsync(_indexCryptoStream).ConfigureAwait(false);
        await PackIndexOperations.WriteFanoutTableAsync(_indexCryptoStream, indexSortedEntries).ConfigureAwait(false);

        // Write object hashes section to index (must be in HashId order)
        await WriteObjectHashesToIndexAsync(_indexCryptoStream, indexSortedEntries, cancellationToken).ConfigureAwait(false);

        // Write pack entries in dependency order, but CRC32 values in HashId order
        await WritePackEntriesAndCrc32SimultaneouslyAsync(packStream, _indexCryptoStream, packSortedEntries, indexSortedEntries, cancellationToken).ConfigureAwait(false);

        // Write pack file offsets section to index (in HashId order)
        await WritePackFileOffsetsToIndexAsync(_indexCryptoStream, indexSortedEntries).ConfigureAwait(false);

        await packStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sorts entries to ensure delta bases are written before their dependents.</summary>
    private List<PackEntry> SortEntriesForDeltaChains(List<PackEntry> entries)
    {
        var result = new List<PackEntry>();
        var processed = new HashSet<HashId>();
        var entryById = entries.ToDictionary(e => e.Id);

        // First pass: Add all non-delta entries (bases)
        foreach (var entry in entries.Where(e => !e.IsDelta))
        {
            result.Add(entry);
            processed.Add(entry.Id);
        }

        // Second pass: Add delta entries in dependency order
        var remaining = entries.Where(e => e.IsDelta).ToList();
        var maxIterations = remaining.Count * 2; // Prevent infinite loops
        var iteration = 0;

        while (remaining.Count > 0 && iteration < maxIterations)
        {
            iteration++;
            var addedThisRound = new List<PackEntry>();

            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var entry = remaining[i];

                // Check if the base is already processed or is in our current list of bases
                if (entry.BaseId != null &&
                    (processed.Contains(entry.BaseId) || entryById.ContainsKey(entry.BaseId)))
                {
                    result.Add(entry);
                    processed.Add(entry.Id);
                    addedThisRound.Add(entry);
                    remaining.RemoveAt(i);
                }
            }

            // If we didn't make progress, add remaining entries to avoid infinite loop
            if (addedThisRound.Count == 0 && remaining.Count > 0)
            {
                _logger?.LogWarning("Could not resolve all delta dependencies, adding remaining {Count} entries", remaining.Count);
                result.AddRange(remaining);
                break;
            }
        }

        _logger?.LogDebug("Sorted {Count} entries for delta chains in {Iterations} iterations", result.Count, iteration);
        return result;
    }

    /// <summary>Writes pack entries and their CRC32 values, handling the different sort orders.</summary>
    /// <remarks>
    /// This method writes both the pack entries and their CRC32 values to the index simultaneously,
    /// but handles the fact that pack entries are in dependency order while index entries must be in HashId order.
    /// </remarks>
    private async Task WritePackEntriesAndCrc32SimultaneouslyAsync(Stream packStream, CryptoStream indexCryptoStream,
        List<PackEntry> packSortedEntries, List<PackEntry> indexSortedEntries, CancellationToken cancellationToken)
    {
        // Create a mapping from entry ID to CRC32 as we write pack entries
        var crc32Map = new Dictionary<HashId, byte[]>();

        // Write pack entries in dependency order
        foreach (var entry in packSortedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Write pack entry and get its CRC32 in one operation
            var crc32Hash = await PackFileOperations.WritePackEntryWithCrc32Async(packStream, entry, _objectOffsets, _logger).ConfigureAwait(false);
            crc32Map[entry.Id] = crc32Hash;

            _logger?.LogDebug("Wrote pack entry {Id} at offset {Offset}", entry.Id, _objectOffsets[entry.Id]);
        }

        // Now write CRC32 values in HashId order (for index)
        foreach (var id in indexSortedEntries.Select(e => e.Id))
        {
            if (crc32Map.TryGetValue(id, out var crc32Hash))
            {
                await indexCryptoStream.WriteAsync(crc32Hash, cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("Wrote CRC32 for entry {Id}", id);
            }
            else
            {
                throw new InvalidOperationException($"CRC32 not found for entry {id}");
            }
        }
    }

    /// <summary>Writes object hashes to the index file.</summary>
    private static async Task WriteObjectHashesToIndexAsync(CryptoStream indexCryptoStream, List<PackEntry> sortedEntries, CancellationToken cancellationToken)
    {
        foreach (var entry in sortedEntries)
        {
            await indexCryptoStream.WriteAsync(entry.Id.Hash.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes pack file offsets to the index file in HashId order.</summary>
    private async Task WritePackFileOffsetsToIndexAsync(CryptoStream indexCryptoStream, List<PackEntry> indexSortedEntries)
    {
        foreach (int offset in indexSortedEntries.Select(entry => _objectOffsets[entry.Id]))
        {
            await PackFileOperations.WriteBigEndianIntAsync(indexCryptoStream, offset).ConfigureAwait(false);
        }
    }

    /// <summary>Finalizes the index file by writing the pack checksum and index checksum.</summary>
    private async Task FinalizeIndexFileAsync(byte[] packChecksum, CancellationToken cancellationToken)
    {
        if (_indexCryptoStream == null || _indexSha1 == null || _indexBaseStream == null)
            throw new InvalidOperationException("Index streams not initialized");

        try
        {
            // Write pack checksum to the index
            await _indexCryptoStream.WriteAsync(packChecksum, cancellationToken).ConfigureAwait(false);

            // Finalize the index crypto stream and get the index checksum
            await _indexCryptoStream.FlushFinalBlockAsync(cancellationToken);
            var indexChecksum = _indexSha1.Hash!;

            // Write the index checksum to the base stream
            await _indexBaseStream.WriteAsync(indexChecksum, cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("Pack index file written successfully");
        }
        finally
        {
            // Clean up streams
            if (_indexCryptoStream is not null) await _indexCryptoStream.DisposeAsync();
            _indexSha1?.Dispose();
            if (_indexBaseStream is not null) await _indexBaseStream.DisposeAsync();

            _indexCryptoStream = null;
            _indexSha1 = null;
            _indexBaseStream = null;
        }
    }

    /// <summary>Computes the SHA-1 checksum of the pack file contents (excluding the final checksum itself).</summary>
    private async Task<byte[]> ComputePackChecksumAsync(string packFilePath, CancellationToken cancellationToken = default)
    {
        using var sha1 = SHA1.Create();
        await using var fileStream = _fileSystem.File.OpenRead(packFilePath);

        // Compute hash of the entire file content
        var hash = await sha1.ComputeHashAsync(fileStream, cancellationToken).ConfigureAwait(false);

        _logger?.LogDebug("Computed pack checksum: {Checksum} for file size: {FileSize} bytes",
            Convert.ToHexString(hash), fileStream.Length);

        return hash;
    }

    private async Task WritePackChecksumAsync(CancellationToken cancellationToken)
    {
        // Write checksum to end of pack file
        await using var fileStream = _fileSystem.File.OpenWrite(_temporaryPackPath);
        fileStream.Seek(0, SeekOrigin.End); // Seek to the end to append
        await fileStream.WriteAsync(_packChecksum, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases all resources used by the PackWriter.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
#pragma warning disable S2139 // Exceptions should be either logged or rethrown but not both
                try
                {
                    // Clean up any remaining index streams
                    _indexCryptoStream?.Dispose();
                    _indexSha1?.Dispose();
                    _indexBaseStream?.Dispose();

                    // If we have a pack checksum, rename temporary files to final names
                    if (_packChecksum != null)
                    {
                        RenameTemporaryFilesToFinalNames();
                    }
                    else
                    {
                        // Clean up temporary files if pack was never written successfully
                        CleanupTemporaryFiles();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error during PackWriter disposal");

                    // Try to clean up temporary files in case of error
                    CleanupTemporaryFilesWithErrorHandling();

                    throw;
                }
#pragma warning restore S2139 // Exceptions should be either logged or rethrown but not both

                _entries.Clear();
                _objectOffsets.Clear();
                _entryCache.Clear();
                _logger?.LogDebug("PackWriter disposed");
            }
            _disposedValue = true;
        }
    }

    private void RenameTemporaryFilesToFinalNames()
    {
        var finalPackPath = FinalPackPath!;
        var finalIndexPath = FinalIndexPath!;

        // Rename pack file and index file
        RenameFileIfExists(_temporaryPackPath, finalPackPath, "pack file");
        RenameFileIfExists(_temporaryIndexPath, finalIndexPath, "index file");
    }

    private void RenameFileIfExists(string temporaryPath, string finalPath, string fileType)
    {
        if (_fileSystem.File.Exists(temporaryPath))
        {
            // Remove existing final file if it exists (shouldn't happen in normal cases)
            if (_fileSystem.File.Exists(finalPath))
            {
                _fileSystem.File.Delete(finalPath);
                _logger?.LogWarning("Removed existing {FileType}: {Path}", fileType, finalPath);
            }

            _fileSystem.File.Move(temporaryPath, finalPath);
            _logger?.LogInformation("Renamed {FileType}: {TempPath} -> {FinalPath}", fileType, temporaryPath, finalPath);
        }
    }

    private void CleanupTemporaryFiles()
    {
        CleanupTemporaryFile(_temporaryPackPath, "pack file");
        CleanupTemporaryFile(_temporaryIndexPath, "index file");
    }

    private void CleanupTemporaryFile(string filePath, string fileType)
    {
        if (_fileSystem.File.Exists(filePath))
        {
            _fileSystem.File.Delete(filePath);
            _logger?.LogInformation("Cleaned up temporary {FileType}: {Path}", fileType, filePath);
        }
    }

    private void CleanupTemporaryFilesWithErrorHandling()
    {
        try
        {
            CleanupTemporaryFiles();
        }
        catch (Exception cleanupEx)
        {
            _logger?.LogError(cleanupEx, "Error cleaning up temporary files");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}