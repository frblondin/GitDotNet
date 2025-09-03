using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace GitDotNet;

/// <summary>
/// Defines the contract for a Git connection, providing access to Git repository operations and data.
/// This interface abstracts the core functionality needed to interact with a Git repository, including
/// branch management, commit operations, object retrieval, and repository comparisons.
/// </summary>
public partial interface IGitConnection : IDisposable
{
    /// <summary>
    /// Gets the collection of branches in the repository, including both local and remote tracking branches.
    /// </summary>
    /// <value>A <see cref="Branch.List"/> containing all branches available in the repository.</value>
    Branch.List Branches { get; }

    /// <summary>
    /// Gets the HEAD branch reference, which points to the currently checked out branch or commit.
    /// </summary>
    /// <value>A <see cref="Branch"/> representing the current HEAD reference.</value>
    Branch Head { get; }

    /// <summary>
    /// Gets the Git index (staging area) for this repository, containing staged changes ready for commit.
    /// </summary>
    /// <value>An <see cref="Index"/> instance representing the repository's staging area.</value>
    Index Index { get; }

    /// <summary>
    /// Gets repository information including configuration, paths, and metadata.
    /// </summary>
    /// <value>A <see cref="RepositoryInfo"/> instance containing repository details and configuration.</value>
    RepositoryInfo Info { get; }

    /// <summary>
    /// Gets the object resolver for retrieving Git objects (commits, trees, blobs, tags) by their hash identifiers.
    /// </summary>
    /// <value>An <see cref="IObjectResolver"/> for accessing Git objects in the repository.</value>
    IObjectResolver Objects { get; }

    /// <summary>
    /// Gets the collection of remote repositories configured for this Git repository.
    /// </summary>
    /// <value>A <see cref="Remote.List"/> containing all configured remote repositories.</value>
    Remote.List Remotes { get; }

    /// <summary>
    /// Commits the currently staged changes with the specified commit message and optional signature information.
    /// </summary>
    /// <param name="message">The commit message describing the changes.</param>
    /// <param name="author">Optional author signature for the commit. If <see langword="null"/>, uses the configured user information.</param>
    /// <param name="committer">Optional committer signature. If <see langword="null"/>, uses the same as author or configured user information.</param>
    /// <param name="options">Optional commit configuration settings.</param>
    /// <returns>A <see cref="Task{CommitEntry}"/> representing the asynchronous commit operation that returns the created commit.</returns>
    /// <exception cref="ArgumentException">Thrown when the commit message is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when there are no staged changes to commit or the commit operation fails.</exception>
    Task<CommitEntry> CommitAsync(string message, Signature? author = null, Signature? committer = null, CommitOptions? options = null);

    /// <summary>
    /// Creates a commit on the specified branch using transformation operations to modify the repository content.
    /// This method is typically used for programmatic commits without requiring a working directory.
    /// </summary>
    /// <param name="branchName">The name of the branch to commit to (can be canonical ref name or short name).</param>
    /// <param name="transformations">A function that applies transformations (add, modify, delete operations) to the repository content.</param>
    /// <param name="commit">The commit object containing metadata (message, author, committer, parents).</param>
    /// <param name="options">Optional commit configuration settings.</param>
    /// <returns>A <see cref="Task{CommitEntry}"/> representing the asynchronous commit operation that returns the created commit.</returns>
    /// <exception cref="ArgumentException">Thrown when the branch name is invalid or transformations are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no transformations are applied or the commit operation fails.</exception>
    Task<CommitEntry> CommitAsync(string branchName, Action<ITransformationComposer> transformations, CommitEntry commit, CommitOptions? options = null);

    /// <summary>
    /// Creates a commit on the specified branch using asynchronous transformation operations to modify the repository content.
    /// This overload supports transformations that require asynchronous operations.
    /// </summary>
    /// <param name="branchName">The name of the branch to commit to (can be canonical ref name or short name).</param>
    /// <param name="transformations">An asynchronous function that applies transformations to the repository content.</param>
    /// <param name="commit">The commit object containing metadata (message, author, committer, parents).</param>
    /// <param name="options">Optional commit configuration settings.</param>
    /// <returns>A <see cref="Task{CommitEntry}"/> representing the asynchronous commit operation that returns the created commit.</returns>
    /// <exception cref="ArgumentException">Thrown when the branch name is invalid or transformations are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no transformations are applied or the commit operation fails.</exception>
    Task<CommitEntry> CommitAsync(string branchName, Func<ITransformationComposer, Task> transformations, CommitEntry commit, CommitOptions? options = null);

    /// <summary>
    /// Compares two commit entries and returns the differences between their associated tree structures.
    /// </summary>
    /// <param name="old">The older commit to compare from. If <see langword="null"/>, treats as empty repository state.</param>
    /// <param name="new">The newer commit to compare to.</param>
    /// <returns>A <see cref="Task{IList}"/> containing the list of changes between the two commits.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="new"/> is <see langword="null"/>.</exception>
    Task<IList<Change>> CompareAsync(CommitEntry? old, CommitEntry @new);

    /// <summary>
    /// Compares two commits specified by their committish references (hash, branch name, tag, etc.) and returns the differences.
    /// </summary>
    /// <param name="old">The older commit reference to compare from. If <see langword="null"/>, treats as empty repository state.</param>
    /// <param name="new">The newer commit reference to compare to.</param>
    /// <returns>A <see cref="Task{IList}"/> containing the list of changes between the two commits.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="new"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the commit references cannot be resolved.</exception>
    Task<IList<Change>> CompareAsync(string? old, string @new);

    /// <summary>
    /// Compares two tree entries and returns the differences between their content and structure.
    /// </summary>
    /// <param name="old">The older tree entry to compare from. If <see langword="null"/>, treats as empty tree.</param>
    /// <param name="new">The newer tree entry to compare to.</param>
    /// <returns>A <see cref="Task{IList}"/> containing the list of changes between the two trees.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="new"/> is <see langword="null"/>.</exception>
    Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry @new);

    /// <summary>
    /// Creates a new commit object with the specified metadata but does not write it to the repository.
    /// This method is useful for preparing commits before applying them with transformation operations.
    /// </summary>
    /// <param name="message">The commit message.</param>
    /// <param name="parents">The list of parent commits for this commit.</param>
    /// <param name="author">Optional author signature. If <see langword="null"/>, uses configured user information.</param>
    /// <param name="committer">Optional committer signature. If <see langword="null"/>, uses the same as author or configured user information.</param>
    /// <returns>A <see cref="CommitEntry"/> representing the created commit object.</returns>
    /// <exception cref="ArgumentException">Thrown when the message is null or empty.</exception>
    CommitEntry CreateCommit(string message, IList<CommitEntry> parents, Signature? author = null, Signature? committer = null);

    /// <summary>
    /// Resolves a committish reference (commit hash, branch name, tag name, or relative reference) to a commit entry.
    /// Supports Git's commit navigation syntax such as HEAD~1, main^2, etc.
    /// </summary>
    /// <param name="committish">The commit reference to resolve (e.g., "HEAD", "main", "abc123", "HEAD~1", "main^2").</param>
    /// <returns>A <see cref="Task{CommitEntry}"/> representing the resolved commit.</returns>
    /// <exception cref="ArgumentException">Thrown when the committish is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the committish cannot be resolved to a valid commit.</exception>
    Task<CommitEntry> GetCommittishAsync(string committish);

    /// <summary>
    /// Retrieves the commit history starting from the specified committish reference, with optional filtering and sorting.
    /// </summary>
    /// <param name="committish">The starting commit reference for the log traversal.</param>
    /// <param name="options">Optional configuration for log filtering, sorting, and traversal behavior.</param>
    /// <param name="token">A cancellation token to cancel the asynchronous enumeration.</param>
    /// <returns>An <see cref="IAsyncEnumerable{LogEntry}"/> for iterating through the commit history.</returns>
    /// <exception cref="ArgumentException">Thrown when the committish is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the committish cannot be resolved or log traversal fails.</exception>
    IAsyncEnumerable<LogEntry> GetLogAsync(string committish, LogOptions? options = null, CancellationToken token = default);

    /// <summary>
    /// Finds the merge base (common ancestor) between two commits, which is useful for merge and rebase operations.
    /// </summary>
    /// <param name="committish1">The first commit reference.</param>
    /// <param name="committish2">The second commit reference.</param>
    /// <returns>A <see cref="Task{CommitEntry}"/> representing the merge base commit, or <see langword="null"/> if no common ancestor exists.</returns>
    /// <exception cref="ArgumentException">Thrown when either committish is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the committish references cannot be resolved.</exception>
    Task<CommitEntry?> GetMergeBaseAsync(string committish1, string committish2);

    /// <summary>
    /// Retrieves all stash entries from the repository, which represent saved working directory and index state.
    /// </summary>
    /// <returns>A <see cref="Task{IReadOnlyList}"/> containing all stash entries in chronological order (newest first).</returns>
    Task<IReadOnlyList<Stash>> GetStashesAsync();
}