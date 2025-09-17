using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;
internal sealed class PackOptimization(
    TreeEntry? previousRootTree,
    Dictionary<HashId, GitPath> entryPaths,
    int maxDepth = PackOptimization.DefaultMaxDeltaDepth,
    int windowSize = DeltaCompression.DefaultWindowSize,
    int slidingWindowSize = PackOptimization.DefaultSlidingWindowSize,
    ILogger<PackOptimization>? logger = null)
{
    /// <summary>Minimum bytes saved to create delta.</summary>
    private const int MinDeltaSavings = 50;

    /// <summary>Gets the default maximum delta chain depth.</summary>
    public const int DefaultMaxDeltaDepth = 50;

    /// <summary>Gets the default sliding window size for recent entries.</summary>
    public const int DefaultSlidingWindowSize = 10;

    // Simple cache for delta scores to avoid recalculating expensive operations
    private sealed record class EntryData(PackEntry Entry, GitPath? Path, Dictionary<uint, List<int>> HashTables)
    {
        public EntryData? Base { get; set; }

        public int Depth
        {
            get
            {
                var result = 0;
                var @base = Base;
                while (@base != null)
                {
                    @base = @base.Base;
                    result++;
                }
                return result;
            }
        }
    }

    /// <summary>Optimizes entries for delta compression by finding best base objects and creating deltas.</summary>
    /// <param name="entries">The entries to optimize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Optimized list of pack entries with delta compression applied.</returns>
    public async Task<List<PackEntry>> OptimizeEntriesForDeltaCompressionAsync(List<PackEntry> entries, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Optimizing entries for delta compression with max depth: {MaxDepth}, previous tree: {HasPreviousTree}",
            maxDepth, previousRootTree != null);

        var result = new List<PackEntry>();

        foreach (var typeGroup in entries.DistinctBy(e => e.Id).GroupBy(e => e.Type))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var optimizedEntries = await ProcessTypeGroupAsync(typeGroup, cancellationToken).ConfigureAwait(false);
            result.AddRange(optimizedEntries);
        }

        logger?.LogDebug("Delta optimization complete. Total entries: {Count}", result.Count);
        return result;
    }

    /// <summary>Processes a group of entries of the same type for delta optimization.</summary>
    private async Task<IList<PackEntry>> ProcessTypeGroupAsync(IGrouping<EntryType, PackEntry> typeGroup, CancellationToken cancellationToken = default)
    {
        var typeEntries = typeGroup.OrderBy(e => e.Data.Length).ToList();
        logger?.LogDebug("Processing {Count} entries of type {Type}", typeEntries.Count, typeGroup.Key);

        var fullData = BuildEntryHashTables(typeEntries);
        var result = new PackEntry[fullData.Length];

        // Prefer partitioning to allow sequential processing within each partition,
        // which helps with sliding window locality
        // Note that depth check might fail when first items of range use last items of previous ranges,
        // since in this case the depth of these dependencies have not yet been updated
        var rangeSize = Math.Max(fullData.Length / Environment.ProcessorCount, slidingWindowSize);
        var partitioner = Partitioner.Create(0, fullData.Length, rangeSize);
        await Parallel.ForEachAsync(partitioner.GetDynamicPartitions(), cancellationToken, async (indices, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            for (int i = indices.Item1; i < indices.Item2; i++)
            {
                var entry = fullData[i];
                var count = Math.Clamp(i - 1, 0, slidingWindowSize);
                var candidates = count > 0 ? Enumerable.Range(1, count).Select(j => fullData[i - j]) : [];
                var optimized = await FindBestDeltaBaseAsync(entry, candidates).ConfigureAwait(false);
                result[i] = optimized;
            }
        }).ConfigureAwait(false);
        return result;
    }

    private EntryData[] BuildEntryHashTables(List<PackEntry> entries)
    {
        var result = new EntryData[entries.Count];

        // Use parallel processing for building hash tables when we have many entries
        Parallel.For(0, entries.Count, i =>
        {
            var entry = entries[i];
            entryPaths.TryGetValue(entry.Id, out var path);
            result[i] = new(entry, path, DeltaCompression.BuildHashTable(entry.Data, windowSize));
        });

        return result;
    }

    private async Task<PackEntry> FindBestDeltaBaseAsync(EntryData target, IEnumerable<EntryData> candidates)
    {
        if (target.Entry.Data.Length < MinDeltaSavings * 2) // Too small for meaningful delta
            return target.Entry;

        // First, try to find a similar object from previous root tree if available
        var (prevTree, prevTreeScore) = await FindBestDeltaBaseFromPreviousTreeAsync(target).ConfigureAwait(false);

        // Then check other candidates, but prefer previous tree candidate if it's good enough
        var (best, score) = prevTreeScore < target.Entry.Data.Length * 0.8 ?
            await FindBestDeltaBaseAmongCandidatesAsync(target, candidates, prevTreeScore).ConfigureAwait(false) :
            default;

        if (prevTreeScore > MinDeltaSavings && prevTreeScore > score)
        {
            // Previous tree candidate is the best
            target.Base = prevTree;
            // Create ref delta, as previous tree objects are not in the current pack
            return await CreateDeltaEntryAsync(target, EntryType.RefDelta).ConfigureAwait(false);
        }
        else if (best != null)
        {
            // Found a better candidate in the current entries
            target.Base = best;
            return await CreateDeltaEntryAsync(target, EntryType.OfsDelta).ConfigureAwait(false);
        }
        return target.Entry;
    }

    private async Task<(EntryData? bestBase, int bestScore)> FindBestDeltaBaseFromPreviousTreeAsync(EntryData target)
    {
        if (previousRootTree == null || target.Path == null)
        {
            return (null, 0);
        }
        try
        {
            // Try to get the object at the same path from the previous tree
            var previousItem = await previousRootTree!.GetFromPathAsync(target.Path!).ConfigureAwait(false);
            if (previousItem == null || previousItem.Mode.EntryType != target.Entry.Type)
            {
                logger?.LogDebug("No previous object found at path {Path}", target.Path);
                return (null, -1);
            }
            var previousEntry = await previousItem.GetEntryAsync<Entry>().ConfigureAwait(false);
            var packEntry = new PackEntry(target.Entry.Type, previousEntry.Id, previousEntry.Data);
            var hashTable = DeltaCompression.BuildHashTable(packEntry.Data, windowSize);

            // Calculate similarity score
            var score = await DeltaCompression.CalculateDeltaScoreAsync(target.Entry.Data, packEntry.Data, hashTable, windowSize).ConfigureAwait(false);
            logger?.LogDebug("Previous tree candidate at path {Path} was found with similarity score: {Score}",
                target.Path, score);
            return (new(packEntry, target.Path, hashTable), score);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Error trying to find previous tree candidate at path {Path}", target.Path);
        }
        return (null, -1);
    }

    private async Task<(EntryData? Best, int Score)> FindBestDeltaBaseAmongCandidatesAsync(EntryData target, IEnumerable<EntryData> candidates, int bestScore)
    {
        var filteredCandidates = candidates
            .Where(c => c.Entry.Data.Length * 50 > target.Entry.Data.Length && c.Entry.Data.Length < target.Entry.Data.Length * 50)
            .Where(c => c.Depth < maxDepth);

        logger?.LogDebug("Evaluating filtered candidates for {TargetId}", target.Entry.Id);

        var syncLock = new object();
        (EntryData? Best, int Score) best = default;
        await Parallel.ForEachAsync(filteredCandidates, async (candidate, ct) =>
        {
            // Short-circuit if we already found a very good match
            if (best.Score > target.Entry.Data.Length * 0.9) return;

            var score = await DeltaCompression.CalculateDeltaScoreAsync(
                target.Entry.Data, candidate.Entry.Data, candidate.HashTables, windowSize).ConfigureAwait(false);
            if (score > MinDeltaSavings && score > best.Score)
            {
                lock (syncLock)
                {
                    if (score > best.Score)
                    {
                        best = (candidate, score);
                    }
                }
                logger?.LogDebug("Found better delta base for {TargetId}: {BaseId} with score {Score}",
                    target.Entry.Id, candidate.Entry.Id, score);
            }
        }).ConfigureAwait(false);

        return best;
    }

    private async Task<PackEntry> CreateDeltaEntryAsync(EntryData target, EntryType type)
    {
        var data = await DeltaCompression.CreateDeltaAsync(target.Entry.Data, target.Base!.Entry.Data, target.Base.HashTables, windowSize).ConfigureAwait(false);
        return new PackEntry(type, target.Entry.Id, data, target.Base.Entry.Id);
    }
}
