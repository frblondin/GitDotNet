using System.Diagnostics.CodeAnalysis;
using GitDotNet.Tools;

namespace GitDotNet;

public partial class GitConnection
{
    /// <summary>Gets the log of commits from the specified reference.</summary>
    /// <param name="committish">The reference to start from.</param>
    /// <param name="options">The options for the log.</param>
    /// <returns>An asynchronous enumerable of commit entries.</returns>
    public IAsyncEnumerable<CommitEntry> GetLogAsync(string committish, LogOptions? options = null)
    {
        var result = GetChildFirstLogAsync(committish, options);
        if (options?.SortBy.HasFlag(LogTraversal.Topological) ?? false)
        {
            result = result.Reverse();
        }
        return result;
    }

    private async IAsyncEnumerable<CommitEntry> GetChildFirstLogAsync(string committish,
        LogOptions? options = null)
    {
        options ??= LogOptions.Default;

        var endingCommits = options.ExcludeReachableFrom != null ?
            await GetCommittishAsync(options.ExcludeReachableFrom) :
            null;

        var commitsToProcess = new Queue<HashId>();
        var processedCommits = new HashSet<HashId>();
        var startCommit = await GetCommittishAsync(committish);
        var previousCommit = startCommit;
        var entryPath = options.Path;
        commitsToProcess.Enqueue(startCommit.Id);

        HashId? lastEntryId = null;
        while (commitsToProcess.Count > 0)
        {
            var currentHash = commitsToProcess.Dequeue();
            var currentCommit = await Objects.GetAsync<CommitEntry>(currentHash);

            if (options.Start.HasValue && currentCommit.CommitTime < options.Start.Value) continue;
            if (options.End.HasValue && currentCommit.CommitTime > options.End.Value) continue;
            if (endingCommits?.Equals(currentCommit) ?? false) continue;

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
            var entry = await currentRoot.GetFromPathAsync(entryPath!);

            // Entry was not found in the current commit, was it deleted or renamed?
            if (entry is null)
            {
                // See if the entry was renamed
                var diff = await CompareAsync(previousCommit, currentCommit);
                var renamed = diff.FirstOrDefault(c => c.Type == ChangeType.Renamed && c.OldPath!.Equals(entryPath));
                if (renamed is not null)
                {
                    // From then on, the entry will be known by the new path
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
            await HandleFirstParentCommitAsync(commitsToProcess, processedCommits, currentCommit);
        }
        else
        {
            await HandleCommitParentsAsync(options, commitsToProcess, processedCommits, currentCommit);
        }
    }

    private static async Task HandleFirstParentCommitAsync(Queue<HashId> commitsToProcess, HashSet<HashId> processedCommits, CommitEntry currentCommit)
    {
        var parents = await currentCommit.GetParentsAsync();
        if (parents.Count > 0)
        {
            var parentHash = parents[0].Id;
            if (processedCommits.Add(parentHash))
                commitsToProcess.Enqueue(parentHash);
        }
    }

    private static async Task HandleCommitParentsAsync(LogOptions options, Queue<HashId> commitsToProcess, HashSet<HashId> processedCommits, CommitEntry currentCommit)
    {
        var parents = (IEnumerable<CommitEntry>)await currentCommit.GetParentsAsync();
        if (options.SortBy.HasFlag(LogTraversal.Time))
            parents = parents.OrderBy(p => p.CommitTime);

        foreach (var parent in parents.Select(p => p.Id))
        {
            var parentHash = parent;
            if (processedCommits.Add(parentHash))
            {
                commitsToProcess.Enqueue(parentHash);
            }
        }
    }

    /// <summary>Retrieves the merge base commit between two specified commits asynchronously.</summary>
    /// <param name="committish1">The identifier of the first commit to compare for the merge base.</param>
    /// <param name="committish2">The commit entry of the second commit used in the comparison.</param>
    /// <returns>Returns the merge base commit entry or null if no merge base exists.</returns>
    public async Task<CommitEntry?> GetMergeBaseAsync(string committish1, string committish2)
    {
        string? result = null;
        GitCliCommand.Execute(Info.Path, $"merge-base {committish1} {committish2}", outputDataReceived: (_, e) =>
        {
            if (e.Data is not null) result = e.Data.Trim();
        });
        return result != null ? await Objects.GetAsync<CommitEntry>(result) : null;
    }

    private enum Continuation
    {
        Continue,
        Break,
        Skip
    }
}