using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using GitDotNet.Readers;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a Git branch.</summary>
public sealed class Branch : IAsyncEnumerable<CommitEntry>, IComparable<Branch>, IEquatable<Branch>
{
    private readonly Func<Task<CommitEntry>> _tipProvider;

    internal Branch(string canonicalName, GitConnection connection, Func<Task<CommitEntry>> tipProvider)
    {
        CanonicalName = canonicalName;
        Connection = connection;
        _tipProvider = tipProvider;
    }

    [ExcludeFromCodeCoverage]
    internal GitConnection Connection { get; }

    /// <summary>Gets the full name of the branch.</summary>
    public string CanonicalName { get; }

    /// <summary>Gets the short name of the branch.</summary>
    public string FriendlyName => Shorten(CanonicalName);

    /// <summary>Gets the remote associated with the branch.</summary>
    public Remote? Remote => GetRemote();

    /// <summary>Gets the full name of the branch.</summary>
    public string? UpstreamBranchCanonicalName => (Connection
        .Info.Config.GetNamedSection("branch", FriendlyName, throwIfNull: false)?.TryGetValue("merge", out var merge) ?? false) ?
        merge :
        null;

    /// <summary>Gets the tip commit of the branch.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ExcludeFromCodeCoverage]
    public CommitEntry? Tip => AsyncHelper.RunSync(GetTipAsync);

    /// <summary>Returns an enumerator that iterates asynchronously through the commits in the branch.</summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <returns>An enumerator that can be used to iterate through the commits in the branch.</returns>
    [ExcludeFromCodeCoverage]
    public IAsyncEnumerator<CommitEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var tip = Tip;
        if (tip is null)
        {
            throw new InvalidOperationException("Branch tip is null.");
        }
        return Connection.GetLogAsync(tip.Id.ToString()).GetAsyncEnumerator(cancellationToken);
    }

    /// <summary>Gets the tip commit of the branch.</summary>
    public async Task<CommitEntry> GetTipAsync() => await _tipProvider();

    /// <summary>
    /// Updates a reference in the Git repository to point to a new commit. This operation modifies the reference to the
    /// specified commit ID.
    /// </summary>
    /// <param name="commit">The parameter represents the new commit that the reference will be updated to.</param>
    [ExcludeFromCodeCoverage]
    public void UpdateRef(CommitEntry commit) =>
        GitCliCommand.Execute(Connection.Info.Path, $"update-ref {CanonicalName} {commit.Id}");

    private Remote? GetRemote()
    {
        var name = (Connection.Info.Config
            .GetNamedSection("branch", FriendlyName, throwIfNull: false)?.TryGetValue("remote", out var remote) ?? false) ?
            remote :
            null;
        return name is not null ? Connection.Remotes[name] : null;
    }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override string ToString() => CanonicalName;

    private static string Shorten(string canonicalName)
    {
        if (canonicalName.LooksLikeLocalBranch())
        {
            return canonicalName.Substring(Reference.LocalBranchPrefix.Length);
        }

        if (canonicalName.LooksLikeRemoteTrackingBranch())
        {
            return canonicalName.Substring(Reference.RemoteTrackingBranchPrefix.Length);
        }

        throw new ArgumentException($"'{canonicalName}' does not look like a valid branch name.");
    }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public int CompareTo(Branch? other) => StringComparer.Ordinal.Compare(CanonicalName, other?.CanonicalName);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public bool Equals(Branch? other) => StringComparer.Ordinal.Equals(CanonicalName, other?.CanonicalName);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override bool Equals(object? obj) => obj is Branch branch && Equals(branch);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalName);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator ==(Branch? left, Branch? right) => Equals(left, right);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator !=(Branch? left, Branch? right) => !Equals(left, right);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator <(Branch? left, Branch? right) => left is not null && right is not null && left.CanonicalName.CompareTo(right.CanonicalName) < 0;

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator <=(Branch? left, Branch? right) => left is not null && right is not null && left.CanonicalName.CompareTo(right.CanonicalName) <= 0;

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator >(Branch? left, Branch? right) => left is not null && right is not null && left.CanonicalName.CompareTo(right.CanonicalName) > 0;

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator >=(Branch? left, Branch? right) => left is not null && right is not null && left.CanonicalName.CompareTo(right.CanonicalName) >= 0;

    /// <summary>Represents a collection of Git branches.</summary>
    [DebuggerDisplay("Count = {Count}")]
    public class List : IReadOnlyCollection<Branch>
    {
        private readonly IRepositoryInfo _info;
        private readonly BranchRefReader _reader;
        private IImmutableDictionary<string, Branch> _branches;

        internal List(IRepositoryInfo info, BranchRefReader reader)
        {
            _info = info;
            _reader = reader;

            _branches = ResetBranches();
        }

        private IImmutableDictionary<string, Branch> ResetBranches() => _branches = _reader.GetBranches();

        /// <summary>Gets the branch with the specified name.</summary>
        /// <param name="name">The canonical or friendly name of the branch.</param>
        public Branch this[string name] =>
            GetBranch(name) ??
            throw new KeyNotFoundException($"Branch '{name}' not found.");

        /// <inheritdoc/>
        [ExcludeFromCodeCoverage]
        public int Count => _branches.Count;

        /// <summary>Attempts to get the branch with the specified name.</summary>
        /// <param name="name">The canonical or friendly name of the branch.</param>
        /// <param name="branch">When this method returns, contains the branch with the specified name, if found; otherwise, null.</param>
        public bool TryGet(string name, [NotNullWhen(true)] out Branch? branch) => (branch = GetBranch(name)) is not null;

        private Branch? GetBranch(string name)
        {
            if (_branches.TryGetValue(name, out var branch) ||
                _branches.TryGetValue($"{Reference.LocalBranchPrefix}{name}", out branch) ||
                _branches.TryGetValue($"{Reference.RemoteTrackingBranchPrefix}{name}", out branch))
            {
                return branch;
            }
            return null;
        }

        /// <summary>
        /// Removes a specified branch from the repository. The removal can be forced or done safely depending on the
        /// provided option.
        /// </summary>
        /// <param name="name">Specifies the branch to be removed from the repository.</param>
        /// <param name="force">Indicates whether the branch should be removed forcefully, bypassing safety checks.</param>
        [ExcludeFromCodeCoverage]
        public void Remove(string name, bool force = false)
        {
            GitCliCommand.Execute(_info.Path, $"branch {(force ? "-D" : "-d")} {name}");
            ResetBranches();
        }

        /// <summary>Creates a new branch with the specified name from a given commit reference.</summary>
        /// <param name="name">Specifies the name of the new branch to be created.</param>
        /// <param name="committish">Indicates the commit reference from which the new branch will be created.</param>
        /// <param name="allowOverWrite">Determines whether to overwrite an existing branch with the same name.</param>
        /// <returns>Returns the newly created branch object.</returns>
        public Branch Add(string name, string committish, bool allowOverWrite = false)
        {
            GitCliCommand.Execute(_info.Path, $"branch {(allowOverWrite ? "--force" : "")} {name} {committish}");
            ResetBranches();
            return this[name];
        }

        /// <inheritdoc/>
        public IEnumerator<Branch> GetEnumerator() => _branches.Values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}