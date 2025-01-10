namespace GitDotNet;

public partial class GitConnection
{
    /// <summary>Gets the log of commits from the specified reference.</summary>
    /// <param name="reference">The reference to start from.</param>
    /// <param name="options">The options for the log.</param>
    /// <returns>An asynchronous enumerable of commit entries.</returns>
    public IAsyncEnumerable<CommitEntry> GetLogAsync(string reference, LogOptions? options = null) =>
        GetLogImplAsync(reference, options);

    internal IAsyncEnumerable<CommitEntry> GetLogImplAsync(string reference,
                                                           LogOptions? options = null,
                                                           BlobEntry? filterByEntry = null)
    {
        var result = GetChildFirstLogAsync(reference, options, filterByEntry);
        if (options?.SortBy.HasFlag(LogTraversal.Topological) ?? false)
        {
            result = result.Reverse();
        }
        return result;
    }

    private async IAsyncEnumerable<CommitEntry> GetChildFirstLogAsync(string reference,
                                                                      LogOptions? options = null,
                                                                      BlobEntry? filterByEntry = null)
    {
        options ??= LogOptions.Default;

        var endingCommits = new HashSet<HashId>();
        foreach (var @ref in options.LastReferences ?? Enumerable.Empty<string>())
        {
            var commit = await GetCommittishAsync(@ref);
            endingCommits.Add(commit.Id);
        }

        var commitsToProcess = new Queue<HashId>();
        var processedCommits = new HashSet<HashId>();
        var startCommit = await GetCommittishAsync(reference);
        var previousCommit = startCommit;
        var root = await startCommit.GetRootTreeAsync();
        var entryPath = filterByEntry is not null ? await root.GetPathToAsync(filterByEntry) : null;
        commitsToProcess.Enqueue(startCommit.Id);

        HashId? lastEntryId = null;
        while (commitsToProcess.Count > 0)
        {
            var currentHash = commitsToProcess.Dequeue();
            var currentCommit = await Objects.GetAsync<CommitEntry>(currentHash);

            if (options.Start.HasValue && currentCommit.CommitTime < options.Start.Value) continue;
            if (options.End.HasValue && currentCommit.CommitTime > options.End.Value) continue;
            if (endingCommits.Contains(currentCommit.Id)) continue;

            // Do not yield the commit if the entity hasn't changed or if entity didn't exist in the previous commit
            (var continuation, entryPath, lastEntryId) = await CheckContinuationAsync(entryPath,
                                                                                      lastEntryId,
                                                                                      currentCommit,
                                                                                      previousCommit);

            if (continuation == Continuation.Break) break;
            if (continuation == Continuation.Continue) yield return currentCommit;
            previousCommit = currentCommit;

            await ApplySortTraversal(options, commitsToProcess, processedCommits, currentCommit);
        }
    }

    private async Task<(Continuation, GitPath? EntryPath, HashId? LastEntryId)> CheckContinuationAsync(GitPath? entryPath, HashId? lastEntryId, CommitEntry currentCommit, CommitEntry previousCommit)
    {
        var result = Continuation.Continue;

        // Filter by entry
        if (entryPath is not null)
        {
            var currentRoot = await currentCommit.GetRootTreeAsync();
            var entry = await currentRoot.GetPathAsync(entryPath!);

            // Entry was not found in the current commit, was it deleted or renamed?
            if (entry is null)
            {
                // See if the entry was renamed
                var diff = await CompareAsync(previousCommit, currentCommit);
                var renamed = diff.FirstOrDefault(c => c.Type == ChangeType.Renamed && c.OldPath!.Equals(entryPath));
                if (renamed is not null)
                {
                    // From then on, the entry will be known by the new path
                    entry = renamed.New;
                    entryPath = renamed.NewPath!;
                }
                else
                {
                    // The entry was deleted, previous ancestors will not possibly have the entry
                    result = Continuation.Break;
                }
            }
            else if (entry.Id.Equals(lastEntryId))
            {
                // The entry hasn't changed, skip the commit as it's not relevant
                result = Continuation.Skip;
            }
            else
            {
                lastEntryId = entry.Id;
            }
        }
        return (result, entryPath, lastEntryId);
    }

    private static async Task ApplySortTraversal(LogOptions options,
                                                 Queue<HashId> commitsToProcess,
                                                 HashSet<HashId> processedCommits,
                                                 CommitEntry currentCommit)
    {
        if (options.SortBy.HasFlag(LogTraversal.FirstParentOnly))
        {
            var parents = await currentCommit.GetParentsAsync();
            if (parents.Count > 0)
            {
                var parentHash = parents[0].Id;
                if (processedCommits.Add(parentHash)) commitsToProcess.Enqueue(parentHash);
            }
        }
        else
        {
            var parents = (IEnumerable<CommitEntry>)await currentCommit.GetParentsAsync();
            if (options.SortBy.HasFlag(LogTraversal.Time)) parents = parents.OrderBy(p => p.CommitTime);
            if (options.SortBy.HasFlag(LogTraversal.Reverse)) parents = parents.Reverse();

            foreach (var parent in parents)
            {
                var parentHash = parent.Id;
                if (processedCommits.Add(parentHash))
                {
                    commitsToProcess.Enqueue(parentHash);
                }
            }
        }
    }

    private enum Continuation
    {
        Continue,
        Break,
        Skip
    }
}