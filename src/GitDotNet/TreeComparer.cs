using Microsoft.Extensions.Options;
using GitDotNet.Tools;
using BlobEntryPath = (GitDotNet.GitPath Path, GitDotNet.TreeEntryItem Blob);
using AddedOrRemovedBlobEntryPath = (bool IsNew, (GitDotNet.GitPath Path, GitDotNet.TreeEntryItem Blob) Element);

namespace GitDotNet;

internal class TreeComparer(IOptions<GitConnection.Options> options) : ITreeComparer
{
    public virtual async Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry? @new)
    {
        var result = new List<Change>();

        var oldBlobs = old is not null ? await old.GetAllBlobEntriesAsync() : null;
        var newBlobs = @new is not null ? await @new.GetAllBlobEntriesAsync() : null;

        FindModifiedBlobsUsingIds(result, oldBlobs, newBlobs);
        FindRenamedBlobsWithSameId(result, oldBlobs, newBlobs);
        await FindRenamedBlobsWithSimilarity(options, result);

        return [.. result.OrderBy(c => c.NewPath ?? c.OldPath)];
    }

    private static void FindModifiedBlobsUsingIds(List<Change> result,
                                                  IEnumerable<BlobEntryPath>? oldBlobs,
                                                  IEnumerable<BlobEntryPath>? newBlobs)
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
                int i = 0;

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

    private static async Task FindRenamedBlobsWithSimilarity(IOptions<GitConnection.Options> options, List<Change> result)
    {
        var addedItems = result.Where(c => c.Type == ChangeType.Added).ToList();
        foreach (var removedItem in result.Where(c => c.Type == ChangeType.Removed).ToList())
        {
            await FindSimilarRenamedBlobs(options, result, addedItems, removedItem);
        }
    }

    private static async Task FindSimilarRenamedBlobs(IOptions<GitConnection.Options> options, List<Change> result, List<Change> addedItems, Change removedItem)
    {
        foreach (var addedItem in addedItems)
        {
            var removedEntry = await removedItem.Old!.GetEntryAsync<BlobEntry>();
            var addedEntry = await addedItem.New!.GetEntryAsync<BlobEntry>();

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

            var flowControl = EvaluateRenameSimilarity(options, result, addedItems, removedItem, addedItem, removedEntry, addedEntry);
            if (!flowControl)
            {
                break;
            }
        }
    }

    private static bool EvaluateRenameSimilarity(IOptions<GitConnection.Options> options, List<Change> result, List<Change> addedItems, Change removedItem, Change addedItem, BlobEntry removedEntry, BlobEntry addedEntry)
    {
        var similarity = LevenshteinDistance.ComputeSimilarity(removedEntry.Data, addedEntry.Data, options.Value.RenameThreshold);
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

internal interface ITreeComparer
{
    Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry? @new);
}
