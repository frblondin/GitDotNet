using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace GitDotNet;

/// <summary>Represents a log entry in a Git repository.</summary>
/// <param name="commitId">The unique identifier (hash) of the commit.</param>
/// <param name="treeId">The unique identifier (hash) of the tree associated with the commit.</param>
/// <param name="parentIds">The list of parent commit hashes for this commit.</param>
/// <param name="commitTime">The timestamp of the commit.</param>
/// <param name="objectResolver">The object resolver used to retrieve Git objects.</param>
[DebuggerDisplay("{CommitId,nq}")]
public class LogEntry(HashId commitId, HashId treeId, IList<HashId> parentIds, DateTimeOffset commitTime, IObjectResolver objectResolver)
    : Entry(EntryType.LogEntry, commitId, [])
{
    private readonly IObjectResolver _objectResolver = objectResolver;
    private CommitEntry? _commit;
    private TreeEntry? _tree;
    private IList<LogEntry>? _parents;

    /// <summary>Gets the unique identifier (hash) of the commit.</summary>
    public HashId CommitId { get; } = commitId;

    /// <summary>Gets the unique identifier (hash) of the tree associated with the commit.</summary>
    public HashId TreeId { get; } = treeId;

    /// <summary>Gets the list of parent commit hashes for this commit.</summary>
    public IList<HashId> ParentIds { get; } = parentIds;

    /// <summary>Gets the timestamp of the commit.</summary>
    public DateTimeOffset CommitTime { get; } = commitTime;

    /// <summary>Asynchronously retrieves the <see cref="CommitEntry"/> associated with this log entry.</summary>
    /// <returns>The task result contains the <see cref="CommitEntry"/> associated with this log entry.</returns>
    public async Task<CommitEntry> GetCommitAsync() => _commit ??= await _objectResolver.GetAsync<CommitEntry>(CommitId);

    /// <summary>Asynchronously retrieves the <see cref="TreeEntry"/> associated with this log entry.</summary>
    /// <returns>The task result contains the <see cref="TreeEntry"/> associated with this log entry.</returns>
    [ExcludeFromCodeCoverage]
    public async Task<TreeEntry> GetRootTreeAsync() => _tree ??= await _objectResolver.GetAsync<TreeEntry>(TreeId);

    /// <summary>Asynchronously retrieves the parent commits of this log entry.</summary>
    /// <returns>The task result contains the parent commits associated with this log entry.</returns>
    public async Task<IList<LogEntry>> GetParentsAsync()
    {
        if (_parents is null)
        {
            var tasks = ParentIds.Select(_objectResolver.GetAsync<LogEntry>);
            _parents = ImmutableList.Create(await Task.WhenAll(tasks));
        }
        return _parents;
    }
}
