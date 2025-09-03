using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using AddedOrRemovedBlobEntryPath = (bool IsNew, (GitDotNet.GitPath Path, GitDotNet.TreeEntryItem Blob) Element);
using BlobEntryPath = (GitDotNet.GitPath Path, GitDotNet.TreeEntryItem Blob);

namespace GitDotNet;

internal class TreeComparer(IOptions<IGitConnection.Options> options, ILogger<TreeComparer>? logger = null) : ITreeComparer
{
    public virtual async Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry? @new)
    {
        logger?.LogDebug("Comparing trees: old={{id={OldId}}}, new={{id={NewId}}}", old?.Id, @new?.Id);
        var result = new List<Change>();

        var oldBlobs = old is not null ? GetDivergingBlobEntries(old, @new).ToEnumerable().ToList() : null;
        var newBlobs = @new is not null ? GetDivergingBlobEntries(@new, old).ToEnumerable().ToList() : null;
        logger?.LogDebug("Found {OldBlobsCount} old blobs and {NewBlobsCount} new blobs.", oldBlobs?.Count ?? 0, newBlobs?.Count ?? 0);

        FindModifiedBlobsUsingIds(result, oldBlobs, newBlobs);
        FindRenamedBlobsWithSameId(result, oldBlobs, newBlobs);
        await FindRenamedBlobsWithSimilarity(options, result).ConfigureAwait(false);

        logger?.LogDebug("CompareAsync completed. Changes found: {ChangeCount}", result.Count);
        return [.. result.OrderBy(c => c.NewPath ?? c.OldPath)];
    }

    private static async IAsyncEnumerable<BlobEntryPath> GetDivergingBlobEntries(TreeEntry tree, TreeEntry? other)
    {
        var channel = Channel.CreateUnbounded<BlobEntryPath>();
        var task = GetDivergingBlobEntries(channel, tree, other, new Stack<string>());
        await foreach (var result in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            yield return result;
        }
        await task.ConfigureAwait(false);
    }

    private static async Task GetDivergingBlobEntries(Channel<BlobEntryPath> channel, TreeEntry tree, TreeEntry? other, Stack<string> path)
    {
        foreach (var item in tree.Children.Except(other?.Children ?? []))
        {
            path.Push(item.Name);
            if (item.Mode.Type == ObjectType.Tree)
            {
                var child = await item.GetEntryAsync<TreeEntry>().ConfigureAwait(false);

                // Look for tree with same name, meaning that a nested object has been modified
                var otherItem = other?.Children.FirstOrDefault(t => t.Name == item.Name && t.Mode.Type == ObjectType.Tree);
                var otherChild = otherItem is not null ? await otherItem.GetEntryAsync<TreeEntry>().ConfigureAwait(false) : null;

                await GetDivergingBlobEntries(channel, child, otherChild, path).ConfigureAwait(false);
            }
            if (item.Mode.Type == ObjectType.RegularFile)
            {
                await channel.Writer.WriteAsync((path.Reverse().ToArray(), item)).ConfigureAwait(false);
            }
            path.Pop();
        }
        if (path.Count == 0) channel.Writer.Complete();
    }

    private static void FindModifiedBlobsUsingIds(List<Change> result,
          IList<BlobEntryPath>? oldBlobs,
          IList<BlobEntryPath>? newBlobs)
    {
        // Do non-intersection between old and new blobs, based on their ids
        var differentIds = (oldBlobs, newBlobs) switch
        {
            (not null, not null) => GetMissingBlobIds(oldBlobs, newBlobs, false)
                .Concat(GetMissingBlobIds(newBlobs, oldBlobs, true)),
            (not null, null) => oldBlobs.Select(x => (IsNew: false, ItemAndPath: x)),
            (null, not null) => newBlobs.Select(x => (IsNew: true, ItemAndPath: x)),
            _ => [],
        };

        // Group items by path to detect which files where added, removed or modified
        var changesGroupedByPath = differentIds.GroupBy(x => x.Item2.Path, x => (x.IsNew, x.Item2.Blob));
        foreach (var c in changesGroupedByPath)
        {
            var oldItem = c.SingleOrDefault(x => !x.IsNew).Blob;
            var newItem = c.SingleOrDefault(x => x.IsNew).Blob;
            var changeType = ClassifyChangeType(oldItem, newItem);

            var change = new Change(changeType,
                                    oldItem != null ? c.Key : null,
                                    newItem != null ? c.Key : null,
                                    oldItem,
                                    newItem);
            result.Add(change);
        }

        static IEnumerable<AddedOrRemovedBlobEntryPath> GetMissingBlobIds(IEnumerable<BlobEntryPath> first,
                                                                        IEnumerable<BlobEntryPath> second,
                                                                        bool isNew) =>
            first.ExceptBy(second.Select(x => x.Blob.Id), x => x.Blob.Id).Select(x => (IsNew: isNew, ItemPath: x));
    }

    private static ChangeType ClassifyChangeType(TreeEntryItem? oldItem, TreeEntryItem? newItem)
    {
        if (oldItem is null || newItem is null)
        {
            return oldItem is not null ? ChangeType.Removed : ChangeType.Added;
        }
        else
        {
            return ChangeType.Modified;
        }
    }

    private static void FindRenamedBlobsWithSameId(List<Change> result, IEnumerable<BlobEntryPath>? oldBlobs, IEnumerable<BlobEntryPath>? newBlobs)
    {
        if (oldBlobs is not null && newBlobs is not null)
        {
            // See if there are two items with the same id but different paths
            var changesGroupedById = from item in oldBlobs.Select(x => (IsNew: false, ItemAndPath: x))
                                          .Concat(newBlobs.Select(x => (IsNew: true, ItemAndPath: x)))
                                     group item by (item.ItemAndPath.Blob.Id, item.ItemAndPath.Path) into groupedByIdAndPath
                                     where groupedByIdAndPath.Count() != 2
                                     from item in groupedByIdAndPath
                                     group item by item.ItemAndPath.Blob.Id into groupedById
                                     where groupedById.Count() > 1
                                     select
                                     (
                                        OldItems: groupedById.Where(x => !x.IsNew).Select(x => x.ItemAndPath).ToList(),
                                        NewItems: groupedById.Where(x => x.IsNew).Select(x => x.ItemAndPath).ToList()
                                     );
            foreach (var (oldItems, newItems) in changesGroupedById)
            {
                if (oldItems.Count == 0 || newItems.Count == 0)
                {
                    // In this case, previous method should have already handled this
                    continue;
                }
                int i;

                // There can be more than one item with the same id but different paths
                // We will set as renamed only the first pair of items
                for (i = 0; i < Math.Min(oldItems.Count, newItems.Count); i++)
                {
                    var change = new Change(ChangeType.Renamed, oldItems[i].Path, newItems[i].Path, oldItems[i].Blob, newItems[i].Blob);
                    result.Add(change);
                }
                // Manage remaining orphaned items as additions/removals... unlikely to happen
                for (; i < Math.Max(oldItems.Count, newItems.Count); i++)
                {
                    var change = i < oldItems.Count ?
                        new Change(ChangeType.Removed, oldItems[i].Path, null, oldItems[i].Blob, null) :
                        new Change(ChangeType.Added, null, newItems[i].Path, null, newItems[i].Blob);
                    result.Add(change);
                }
            }
        }
    }

    private static async Task FindRenamedBlobsWithSimilarity(IOptions<IGitConnection.Options> options, List<Change> result)
    {
        var addedItems = result.Where(c => c.Type == ChangeType.Added).ToList();
        var removedItems = result.Where(c => c.Type == ChangeType.Removed).ToList();
        var chunkCache = new Dictionary<BlobEntry, HashSet<int>>();
        foreach (var removedItem in removedItems)
        {
            await FindSimilarRenamedBlobs(options, result, addedItems, removedItem, chunkCache).ConfigureAwait(false);
        }
    }

    private static async Task FindSimilarRenamedBlobs(IOptions<IGitConnection.Options> options, List<Change> result, List<Change> addedItems, Change removedItem, IDictionary<BlobEntry, HashSet<int>> chunkCache)
    {
        foreach (var addedItem in addedItems)
        {
            var removedEntry = await removedItem.Old!.GetEntryAsync<BlobEntry>().ConfigureAwait(false);
            var addedEntry = await addedItem.New!.GetEntryAsync<BlobEntry>().ConfigureAwait(false);

            // If size difference is too big, skip levenshtein distance calculation
            if (removedEntry.Data.Length > 0 || addedEntry.Data.Length > 0)
            {
                var diff = (float)Math.Abs(removedEntry.Data.Length - addedEntry.Data.Length);
                var ratio = 1f - diff / Math.Max(removedEntry.Data.Length, addedEntry.Data.Length);
                if (ratio < options.Value.RenameThreshold)
                {
                    continue;
                }
            }

            // If any of the entries is not text, skip levenshtein distance calculation
            if (!removedEntry.IsText || !addedEntry.IsText)
            {
                continue;
            }

            var flowControl = EvaluateRenameSimilarity(addedItem, removedEntry, addedEntry);
            if (!flowControl)
            {
                break;
            }
        }

        bool EvaluateRenameSimilarity(Change addedItem, BlobEntry removedEntry, BlobEntry addedEntry)
        {
            var similarity = EvaluateBlobSimilarity(removedEntry, addedEntry, chunkCache);
            if (similarity >= options.Value.RenameThreshold)
            {
                var renameChange = new Change(ChangeType.Renamed, removedItem.OldPath, addedItem.NewPath, removedItem.Old, addedItem.New);
                result.Remove(removedItem);
                result.Remove(addedItem);
                result.Add(renameChange);
                addedItems.Remove(addedItem);
                return false;
            }

            return true;
        }
    }

    private static float EvaluateBlobSimilarity(BlobEntry a, BlobEntry b, IDictionary<BlobEntry, HashSet<int>> chunkCache)
    {
        var aChunks = GetChunks(a, chunkCache);
        var bChunks = GetChunks(b, chunkCache);
        var commonLines = CountIntersections(aChunks, bChunks);
        var similarity = 2F * commonLines / (aChunks.Count + bChunks.Count);
        return similarity;
    }

    private static HashSet<int> GetChunks(BlobEntry entry, IDictionary<BlobEntry, HashSet<int>> chunkCache) =>
        chunkCache.TryGetValue(entry, out var chunks) ?
        chunks :
        chunkCache[entry] = SplitIntoChunkHashes(entry.Data);

    private static int CountIntersections(HashSet<int> first, HashSet<int> second) =>
        (from value in first
         where second.Contains(value)
         select value).Count();

    private static HashSet<int> SplitIntoChunkHashes(byte[] data)
    {
        var result = new HashSet<int>();
        int start = 0;
        int lineEnding;
        while ((lineEnding = Array.IndexOf(data, (byte)'\n', start)) != -1)
        {
            result.Add(ComputeHash(data.AsSpan(start, lineEnding - start)));
            start = lineEnding + 1;
        }

        // Handle the last chunk if it doesn't end with a newline
        if (start < data.Length)
        {
            result.Add(ComputeHash(data.AsSpan(start)));
        }
        return result;
    }

    private static int ComputeHash(Span<byte> data)
    {
        unchecked
        {
            // The prime number used to compute the FNV hash.
            const int Prime = 16777619;
            // The starting point of the FNV hash.
            const long Offset = 2166136261;

            int hash = (int)Offset;

            foreach (var t in data)
            {
                if (t != (byte)'\r') // Ignore \r
                {
                    hash = (hash ^ t) * Prime;
                }
            }

            return hash;
        }
    }
}

internal interface ITreeComparer
{
    Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry? @new);
}
