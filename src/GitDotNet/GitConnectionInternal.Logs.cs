using System.Runtime.CompilerServices;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet;

internal partial class GitConnectionInternal
{
    public IAsyncEnumerable<LogEntry> GetLogAsync(string committish, LogOptions? options = null,
        CancellationToken token = default)
    {
        _logger?.LogInformation("Getting log for committish: {Committish} with options: {Options}", committish, options);
        var result = GetChildFirstLogAsync(committish, options, token);
        if (options?.SortBy.HasFlag(LogTraversal.Topological) ?? false)
        {
            _logger?.LogDebug("Reversing log result due to Topological sort option.");
            result = result.Reverse();
        }
        return result;
    }

    private async IAsyncEnumerable<LogEntry> GetChildFirstLogAsync(string committish, LogOptions? options = null,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        options ??= LogOptions.Default;
        _logger?.LogDebug("Starting child-first log traversal for committish: {Committish}", committish);

        var endingCommits = options.ExcludeReachableFrom != null ?
            await GetCommittishAsync(options.ExcludeReachableFrom).ConfigureAwait(false) :
            null;

        var commitsToProcess = new Queue<LogEntry>();
        var processedCommits = new HashSet<LogEntry>();
        var startCommit = await GetCommittishAsync<LogEntry>(committish, e => e.ParentIds).ConfigureAwait(false);
        var previousCommit = startCommit;
        var entryPath = options.Path;
        commitsToProcess.Enqueue(startCommit);

        HashId? lastEntryId = null;
        while (commitsToProcess.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var currentCommit = commitsToProcess.Dequeue();
            _logger?.LogDebug("Processing commit: {CommitId}", currentCommit.CommitId);

            if (options.Start.HasValue && currentCommit.CommitTime < options.Start.Value) continue;
            if (options.End.HasValue && currentCommit.CommitTime > options.End.Value) continue;
            if (endingCommits?.Equals(currentCommit) ?? false) continue;

            (var continuation, entryPath, lastEntryId) = await CheckContinuationAsync(entryPath,
                                                                                      lastEntryId,
                                                                                      currentCommit,
                                                                                      previousCommit).ConfigureAwait(false);
            if (continuation == Continuation.Break)
            {
                _logger?.LogInformation("Breaking log traversal at commit: {CommitId}", currentCommit.CommitId);
                break;
            }
            if (continuation == Continuation.Continue)
            {
                _logger?.LogDebug("Yielding commit: {CommitId}", currentCommit.CommitId);
                yield return currentCommit;
            }
            previousCommit = currentCommit;
            await ApplySortTraversalAsync(options, commitsToProcess, processedCommits, currentCommit).ConfigureAwait(false);
        }
    }

    private async Task<(Continuation, GitPath? EntryPath, HashId? LastEntryId)> CheckContinuationAsync(GitPath? entryPath, HashId? lastEntryId, LogEntry currentCommit, LogEntry previousCommit)
    {
        var result = Continuation.Continue;
        if (entryPath is null)
        {
            return (result, entryPath, lastEntryId);
        }

        var currentRoot = await currentCommit.GetRootTreeAsync().ConfigureAwait(false);
        var entry = await currentRoot.GetFromPathAsync(entryPath!).ConfigureAwait(false);
        if (entry is null)
        {
            var diff = await CompareAsync(
                await previousCommit.GetCommitAsync().ConfigureAwait(false),
                await currentCommit.GetCommitAsync().ConfigureAwait(false)).ConfigureAwait(false);
            var renamed = diff.FirstOrDefault(c => c.Type == ChangeType.Renamed && c.OldPath!.Equals(entryPath));
            if (renamed is not null)
            {
                entryPath = renamed.NewPath!;
                _logger?.LogInformation("Entry path renamed from {OldPath} to {NewPath}", entryPath, renamed.NewPath);
            }
            else
            {
                result = Continuation.Break;
                _logger?.LogInformation("Entry path {EntryPath} deleted at commit: {CommitId}", entryPath, currentCommit.CommitId);
            }
        }
        else if (entry.Id.Equals(lastEntryId))
        {
            result = Continuation.Skip;
            _logger?.LogDebug("Entry {EntryPath} unchanged in commit: {CommitId}", entryPath, currentCommit.CommitId);
        }
        else
        {
            lastEntryId = entry.Id;
        }
        return (result, entryPath, lastEntryId);
    }

    public async Task<CommitEntry?> GetMergeBaseAsync(string committish1, string committish2)
    {
        _logger?.LogInformation("Finding merge base between {Committish1} and {Committish2}", committish1, committish2);
        string? result = null;
        GitCliCommand.Execute(Info.Path, $"merge-base {committish1} {committish2}", outputDataReceived: (_, e) =>
        {
            if (e.Data is not null) result = e.Data.Trim();
        });
        if (result != null)
        {
            _logger?.LogDebug("Merge base found: {Result}", result);
            return await Objects.GetAsync<CommitEntry>(result).ConfigureAwait(false);
        }
        _logger?.LogWarning("No merge base found between {Committish1} and {Committish2}", committish1, committish2);
        return null;
    }

    private static async Task ApplySortTraversalAsync(LogOptions options,
                                                 Queue<LogEntry> commitsToProcess,
                                                 HashSet<LogEntry> processedCommits,
                                                 LogEntry currentCommit)
    {
        if (options.SortBy.HasFlag(LogTraversal.FirstParentOnly))
        {
            await HandleFirstParentCommitAsync(commitsToProcess, processedCommits, currentCommit).ConfigureAwait(false);
        }
        else
        {
            await HandleCommitParentsAsync(options, commitsToProcess, processedCommits, currentCommit).ConfigureAwait(false);
        }
    }

    private static async Task HandleFirstParentCommitAsync(Queue<LogEntry> commitsToProcess, HashSet<LogEntry> processedCommits, LogEntry currentCommit)
    {
        var parents = await currentCommit.GetParentsAsync().ConfigureAwait(false);
        if (parents.Count > 0 && processedCommits.Add(parents[0]))
        {
            commitsToProcess.Enqueue(parents[0]);
        }
    }

    private static async Task HandleCommitParentsAsync(LogOptions options, Queue<LogEntry> commitsToProcess, HashSet<LogEntry> processedCommits, LogEntry currentCommit)
    {
        var parents = (IEnumerable<LogEntry>)await currentCommit.GetParentsAsync().ConfigureAwait(false);
        if (options.SortBy.HasFlag(LogTraversal.Time))
            parents = parents.OrderBy(p => p.CommitTime);

        foreach (var parent in parents.Where(processedCommits.Add))
        {
            commitsToProcess.Enqueue(parent);
        }
    }

    private enum Continuation
    {
        Continue,
        Break,
        Skip
    }
}