using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;

/// <summary>Handles delta optimization for pack entries.</summary>
internal sealed class PackOptimization(
    TreeEntry? previousRootTree,
    Dictionary<HashId, GitPath> entryPaths,
    int maxDepth = PackOptimization.DefaultMaxDeltaDepth,
    int windowSize = DeltaCompression.DefaultWindowSize,
    ILogger<PackOptimization>? logger = null)
{
    private const int MinDeltaSavings = 50; // Minimum bytes saved to create delta
    private const int MaxDeltaDepth = 50; // Maximum allowed delta chain depth for safety

    /// <summary>Gets the default maximum delta chain depth.</summary>
    public const int DefaultMaxDeltaDepth = 10;

    private readonly int _maxDepth = Math.Clamp(maxDepth, 1, MaxDeltaDepth);
    private readonly HashSet<HashId> _processedEntries = [];
    private readonly Dictionary<HashId, int> _deltaChainDepth = []; // Track delta chain depths

    // Simple cache for delta scores to avoid recalculating expensive operations
    private readonly Dictionary<(HashId target, HashId source), int> _deltaScoreCache = [];
    private const int MaxCacheSize = 10000; // Limit cache size to prevent memory issues

    /// <summary>Optimizes entries for delta compression by finding best base objects and creating deltas.</summary>
    /// <param name="entries">The entries to optimize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Optimized list of pack entries with delta compression applied.</returns>
    public async Task<List<PackEntry>> OptimizeEntriesForDeltaCompressionAsync(List<PackEntry> entries, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Optimizing entries for delta compression with max depth: {MaxDepth}, previous tree: {HasPreviousTree}",
            _maxDepth, previousRootTree != null);

        var result = new List<PackEntry>();

        foreach (var typeGroup in entries.GroupBy(e => e.Type))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessTypeGroupAsync(typeGroup, result).ConfigureAwait(false);
        }

        logger?.LogDebug("Delta optimization complete. Total entries: {Count}", result.Count);
        return result;
    }

    /// <summary>Processes a group of entries of the same type for delta optimization.</summary>
    private async Task ProcessTypeGroupAsync(IGrouping<EntryType, PackEntry> typeGroup, List<PackEntry> result)
    {
        var typeEntries = typeGroup.OrderBy(e => e.Data.Length).ToList();
        logger?.LogDebug("Processing {Count} entries of type {Type}", typeEntries.Count, typeGroup.Key);

        var hashTableCache = DeltaCompression.BuildEntryHashTables(typeEntries, windowSize);

        // Process in batches to avoid memory pressure with large numbers of entries
        var batchSize = Math.Min(100, typeEntries.Count);
        for (int batchStart = 0; batchStart < typeEntries.Count; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, typeEntries.Count);
            var batch = typeEntries.GetRange(batchStart, batchEnd - batchStart);

            foreach (var entry in batch.Where(entry => !_processedEntries.Contains(entry.Id)))
            {
                await ProcessSingleEntryAsync(entry, typeEntries, hashTableCache, result).ConfigureAwait(false);
            }

            // Clear some cache entries between batches to manage memory
            if (_deltaScoreCache.Count <= MaxCacheSize * 0.8)
            {
                continue;
            }

            var keysToRemove = _deltaScoreCache.Keys.Take(MaxCacheSize / 4).ToList();
            foreach (var keyToRemove in keysToRemove)
            {
                _deltaScoreCache.Remove(keyToRemove);
            }
        }
    }

    /// <summary>Processes a single entry for delta optimization.</summary>
    private async Task ProcessSingleEntryAsync(PackEntry entry, List<PackEntry> typeEntries,
        Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache, List<PackEntry> result)
    {
        var deltaBase = await FindBestDeltaBaseAsync(entry, typeEntries, hashTableCache).ConfigureAwait(false);

        if (deltaBase != null)
        {
            // Check if base is already processed to determine delta type
            var useOfsDelta = _processedEntries.Contains(deltaBase.Id);

            var deltaEntry = await CreateDeltaEntryAsync(entry, deltaBase, hashTableCache, useOfsDelta).ConfigureAwait(false);
            if (deltaEntry != null)
            {
                AddDeltaEntryWithBase(deltaEntry, deltaBase, result, typeEntries);
                return;
            }
        }

        // No suitable delta base found, add as regular entry
        AddRegularEntry(entry, result);
    }

    /// <summary>Adds a delta entry along with its base to the result, ensuring the base is added first.</summary>
    private void AddDeltaEntryWithBase(PackEntry deltaEntry, PackEntry baseEntry,
        List<PackEntry> result, List<PackEntry> typeEntries)
    {
        // Only add the base entry if it's from our original entries list (not from previous tree)
        var isBaseFromOriginalEntries = typeEntries.Any(e => e.Id.Equals(baseEntry.Id));

        if (isBaseFromOriginalEntries && !_processedEntries.Contains(baseEntry.Id))
        {
            result.Add(baseEntry);
            _processedEntries.Add(baseEntry.Id);
            logger?.LogDebug("Added base entry: {BaseId}", baseEntry.Id);
        }

        result.Add(deltaEntry);
        _processedEntries.Add(deltaEntry.Id);

        // Track delta chain depth - this entry's depth is base depth + 1
        var baseDepth = _deltaChainDepth.GetValueOrDefault(baseEntry.Id, 0);
        var currentDepth = baseDepth + 1;
        _deltaChainDepth[deltaEntry.Id] = currentDepth;

        logger?.LogDebug("Added delta entry: {EntryId} -> {BaseId} (depth: {Depth}) [base from previous tree: {FromPreviousTree}]",
            deltaEntry.Id, baseEntry.Id, currentDepth, !isBaseFromOriginalEntries);
    }

    /// <summary>Adds a regular (non-delta) entry to the result.</summary>
    private void AddRegularEntry(PackEntry entry, List<PackEntry> result)
    {
        result.Add(entry);
        _processedEntries.Add(entry.Id);
        _deltaChainDepth[entry.Id] = 0; // Base entries have depth 0
    }

    private async Task<PackEntry?> FindBestDeltaBaseAsync(PackEntry target, List<PackEntry> candidates,
        Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache)
    {
        if (target.Data.Length < MinDeltaSavings * 2) // Too small for meaningful delta
            return null;

        // First, try to find a similar object from previous root tree if available
        var (bestBase, bestScore) = await FindBestDeltaBaseFromPreviousTreeAsync(
            target, hashTableCache, MinDeltaSavings).ConfigureAwait(false);

        // Early termination if we found a very good match from previous tree
        if (bestScore > target.Data.Length * 0.8) // 80% similarity is excellent
        {
            return bestBase;
        }

        // Then check other candidates, but prefer previous tree candidate if it's good enough
        bestBase = await FindBestDeltaBaseAmongCandidatesAsync(
            target, candidates, hashTableCache, MinDeltaSavings, bestBase, bestScore).ConfigureAwait(false);

        return bestBase;
    }

    private async Task<(PackEntry? bestBase, int bestScore)> FindBestDeltaBaseFromPreviousTreeAsync(
        PackEntry target, Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache, int minSavings)
    {
        if (previousRootTree == null || !entryPaths.TryGetValue(target.Id, out var targetPath))
        {
            return (null, 0);
        }

        var previousCandidate = await TryFindPreviousTreeCandidateAsync(target, targetPath, hashTableCache, minSavings).ConfigureAwait(false);
        if (previousCandidate == null)
        {
            return (null, 0);
        }

        var bestBase = previousCandidate;
        var bestScore = await DeltaCompression.CalculateDeltaScoreAsync(
            target.Data, previousCandidate.Data, hashTableCache[previousCandidate.Id], windowSize).ConfigureAwait(false);
        logger?.LogDebug("Found previous tree candidate for {TargetId} at path {Path}: score {Score}",
            target.Id, targetPath, bestScore);
        return (bestBase, bestScore);

    }

    private async Task<PackEntry?> FindBestDeltaBaseAmongCandidatesAsync(PackEntry target, List<PackEntry> candidates,
        Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache, int minSavings, PackEntry? bestBase, int bestScore)
    {
        // Pre-filter candidates by size to avoid expensive comparisons
        var suitableCandidates = candidates.Where(candidate =>
        {
            if (candidate.Id == target.Id || _processedEntries.Contains(candidate.Id))
                return false;

            // Size-based filtering: base shouldn't be more than 2x target size or less than 0.1x
            var sizeRatio = (double)candidate.Data.Length / target.Data.Length;
            if (sizeRatio > 2.0 || sizeRatio < 0.1)
                return false;

            // Check if adding this delta would exceed max depth
            var candidateDepth = _deltaChainDepth.GetValueOrDefault(candidate.Id, 0);
            return candidateDepth < _maxDepth;
        })
        .OrderBy(c => Math.Abs(c.Data.Length - target.Data.Length)) // Sort by size similarity
        .Take(Math.Min(50, candidates.Count / 4)) // Limit to at most 50 candidates or 25% of total
        .ToList();

        foreach (var candidate in from candidate in suitableCandidates
                                            let sizeDiff = Math.Abs(candidate.Data.Length - target.Data.Length)
                                            where sizeDiff <= target.Data.Length * 0.5
                                            select candidate)
        {
            var score = await GetCachedDeltaScoreAsync(target, candidate, hashTableCache).ConfigureAwait(false);

            // Only replace if significantly better than previous tree candidate
            if (score <= bestScore || score <= minSavings)
            {
                continue;
            }

            bestScore = score;
            bestBase = candidate;

            // Early termination if we found a very good match
            if (score > target.Data.Length * 0.9) // 90% similarity is excellent
            {
                break;
            }
        }

        return bestBase;
    }

    private async Task<int> GetCachedDeltaScoreAsync(PackEntry target, PackEntry source,
        Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache)
    {
        var cacheKey = (target.Id, source.Id);

        // Check cache first
        if (_deltaScoreCache.TryGetValue(cacheKey, out var cachedScore))
        {
            return cachedScore;
        }

        // Calculate score if not cached
        var score = await DeltaCompression.CalculateDeltaScoreAsync(
            target.Data, source.Data, hashTableCache[source.Id], windowSize).ConfigureAwait(false);

        switch (_deltaScoreCache.Count)
        {
            // Cache the result (with size limit)
            case < MaxCacheSize:
                _deltaScoreCache[cacheKey] = score;
                break;
            case MaxCacheSize:
            {
                // Clear some cache entries when we hit the limit
                var keysToRemove = _deltaScoreCache.Keys.Take(MaxCacheSize / 4).ToList();
                foreach (var keyToRemove in keysToRemove)
                {
                    _deltaScoreCache.Remove(keyToRemove);
                }
                _deltaScoreCache[cacheKey] = score;
                break;
            }
        }

        return score;
    }

    /// <summary>Tries to find a candidate from the previous root tree at the same path.</summary>
    private async Task<PackEntry?> TryFindPreviousTreeCandidateAsync(PackEntry target, GitPath targetPath,
        Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache, int minSavings)
    {
        try
        {
            // Try to get the object at the same path from the previous tree
            var previousItem = await previousRootTree!.GetFromPathAsync(targetPath).ConfigureAwait(false);
            if (previousItem == null)
            {
                logger?.LogDebug("No previous object found at path {Path}", targetPath);
                return null;
            }

            // Check if it's the same entry type
            if (previousItem.Mode.EntryType != target.Type)
            {
                logger?.LogDebug("Previous object at path {Path} has different type: {PreviousType} vs {TargetType}",
                    targetPath, previousItem.Mode.EntryType, target.Type);
                return null;
            }

            // Get the actual entry data
            Entry? previousEntry = target.Type switch
            {
                EntryType.Blob => await previousItem.GetEntryAsync<BlobEntry>().ConfigureAwait(false),
                EntryType.Tree => await previousItem.GetEntryAsync<TreeEntry>().ConfigureAwait(false),
                _ => null
            };

            if (previousEntry == null)
            {
                logger?.LogDebug("Could not retrieve previous entry at path {Path}", targetPath);
                return null;
            }

            // Create a PackEntry for the previous object
            var previousPackEntry = new PackEntry(target.Type, previousEntry.Id, previousEntry.Data);

            // Build hash table if not already cached
            if (!hashTableCache.TryGetValue(previousPackEntry.Id, out var value))
            {
                value = DeltaCompression.BuildHashTable(previousPackEntry.Data, windowSize);
                hashTableCache[previousPackEntry.Id] = value;
            }

            // Calculate similarity score
            var score = await DeltaCompression.CalculateDeltaScoreAsync(target.Data, previousPackEntry.Data, value, windowSize).ConfigureAwait(false);

            if (score > minSavings)
            {
                logger?.LogDebug("Previous tree candidate at path {Path} has good similarity score: {Score}", targetPath, score);
                return previousPackEntry;
            }

            logger?.LogDebug("Previous tree candidate at path {Path} has insufficient similarity score: {Score} < {MinSavings}",
                targetPath, score, minSavings);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Error trying to find previous tree candidate at path {Path}", targetPath);
        }

        return null;
    }

    private async Task<PackEntry?> CreateDeltaEntryAsync(PackEntry target, PackEntry baseEntry,
        Dictionary<HashId, Dictionary<uint, List<int>>> hashTableCache, bool useOfsDelta)
    {
        var deltaData = await DeltaCompression.CreateDeltaAsync(target.Data, baseEntry.Data, hashTableCache[baseEntry.Id], windowSize).ConfigureAwait(false);

        if (deltaData.Length >= target.Data.Length - MinDeltaSavings)
            return null; // Delta not worth it

        // Choose delta type: OfsDelta is more efficient when base is already written
        var deltaType = useOfsDelta ? EntryType.OfsDelta : EntryType.RefDelta;

        logger?.LogDebug("Created delta: original={OriginalSize}, delta={DeltaSize}, savings={Savings}, type={DeltaType}",
            target.Data.Length, deltaData.Length, target.Data.Length - deltaData.Length, deltaType);

        return new PackEntry(deltaType, target.Id, deltaData, baseEntry.Id);
    }
}