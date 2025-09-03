using System.Diagnostics.CodeAnalysis;

namespace GitDotNet;

/// <summary>Represents a Git stash entry.</summary>
public class Stash : CommitEntry
{
    private readonly IGitConnection _connection;

    internal Stash(CommitEntry commit, IGitConnection connection) : base(commit.Id, commit.Data, commit.ObjectResolver, commit.RootTree)
    {
        _connection = connection;
    }

    /// <summary>Retrieves the HEAD commit when stashing (HEAD at stash time).</summary>
    /// <returns>A <see cref="CommitEntry"/> object representing the head commit when stashing.</returns>
    public async Task<CommitEntry> GetHeadAsync() => (await GetParentsAsync().ConfigureAwait(false))[0];

    /// <summary>Retrieves the staged commit entry.</summary>
    /// <remarks>This method returns the second parent commit in the list of parent commits. Ensure that the
    /// repository has at least two parent commits before calling this method.</remarks>
    /// <returns>A <see cref="CommitEntry"/> representing the staged commit.</returns>
    public async Task<CommitEntry> GetStagedCommitAsync() => (await GetParentsAsync().ConfigureAwait(false))[1];

    /// <summary>Retrieves the commit containing untracked file additions, if available.</summary>
    /// <returns>A <see cref="CommitEntry"/> representing the untracked file changes, if any.</returns>
    public async Task<CommitEntry?> GetUntrackedCommitAsync()
    {
        var parents = await GetParentsAsync().ConfigureAwait(false);
        return parents.Count > 2 ? parents[2] : null;
    }

    /// <summary>
    /// Retrieves a list of changes staged for the next commit by comparing the base commit to the staged changes
    /// commit.
    /// </summary>
    /// <remarks>This method performs an asynchronous comparison between the specified base commit and the
    /// staged changes commit to identify the changes that are staged for inclusion in the next commit. The returned
    /// list includes all staged changes, such as added, modified, or deleted files.</remarks>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="Change"/>
    /// objects representing the staged changes. If no changes are staged, the list will be empty.</returns>
    public async Task<IList<Change>> GetStagedChangesAsync() => await _connection.CompareAsync(
        await GetHeadAsync().ConfigureAwait(false),
        await GetStagedCommitAsync().ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>Retrieves a list of changes that have not been staged for commit.</summary>
    /// <remarks>This method compares the current state of the repository with the base commit to identify
    /// changes that are present but not yet staged. The returned list includes details about each change, such as file
    /// paths and change types (e.g., added, modified, or deleted).</remarks>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="Change"/>
    /// objects representing the unstaged changes. If no changes are found, the list will be empty.</returns>
    public async Task<IList<Change>> GetUnStagedChangesAsync()
    {
        var stagedCommit = await GetStagedCommitAsync().ConfigureAwait(false);
        return await _connection.CompareAsync(stagedCommit, this).ConfigureAwait(false);
    }

    /// <summary>Retrieves a list of changes that are not currently tracked.</summary>
    /// <remarks>This method compares the staged commit with the untracked commit to identify changes that
    /// have not been staged. If no untracked commit exists, the method returns an empty list.</remarks>
    /// <returns>A list of <see cref="Change"/> objects representing the untracked changes. Returns an empty list if no untracked
    /// commit is available.</returns>
    public async Task<IList<Change>> GetUntrackedChangesAsync()
    {
        var stagedCommit = await GetStagedCommitAsync().ConfigureAwait(false);
        var untrackedCommit = await GetUntrackedCommitAsync().ConfigureAwait(false);
        return untrackedCommit is not null ?
            await _connection.CompareAsync(stagedCommit, untrackedCommit).ConfigureAwait(false) :
            [];
    }

    /// <summary>Retrieves a list of changes, including both staged and unstaged changes.</summary>
    /// <remarks>This method asynchronously combines staged and unstaged changes into a single list. The
    /// returned list may include changes from multiple sources, such as files that have been modified, added, or
    /// deleted.</remarks>
    /// <param name="includeUntracked">Include untracked file additions.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="Change"/>
    /// objects representing the staged and unstaged changes.</returns>
    public async Task<IList<Change>> GetChangesAsync(bool includeUntracked) =>
        [.. await GetStagedChangesAsync().ConfigureAwait(false),
         .. await GetUnStagedChangesAsync().ConfigureAwait(false),
         .. includeUntracked ? await GetUntrackedChangesAsync() : []];
}