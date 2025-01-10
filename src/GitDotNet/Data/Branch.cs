using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a Git branch.</summary>
public class Branch : IAsyncEnumerable<CommitEntry>, IComparable<Branch>
{
    private readonly Func<Task<CommitEntry>> _tipProvider;

    internal Branch(string canonicalName, GitConnection connection, Func<Task<CommitEntry>> tipProvider)
    {
        CanonicalName = canonicalName;
        Connection = connection;
        _tipProvider = tipProvider;
    }

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
    public CommitEntry? Tip => AsyncHelper.RunSync(GetTipAsync);

    /// <summary>Returns an enumerator that iterates asynchronously through the commits in the branch.</summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <returns>An enumerator that can be used to iterate through the commits in the branch.</returns>
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

    private Remote GetRemote()
    {
        var name = (Connection.Info.Config
            .GetNamedSection("branch", FriendlyName, throwIfNull: false)?.TryGetValue("remote", out var remote) ?? false) ?
            remote :
            null;
        return name is not null ? Connection.Remotes[name] : null;
    }

    /// <inheritdoc/>
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
    public int CompareTo(Branch? other) => CanonicalName.CompareTo(other?.CanonicalName);

    /// <summary>Represents a collection of Git branches.</summary>
    [DebuggerDisplay("Count = {Count}")]
    public class List : IReadOnlyCollection<Branch>
    {
        private readonly IImmutableDictionary<string, Branch> _branches;

        internal List(IImmutableDictionary<string, Branch> branches)
        {
            _branches = branches;
        }

        /// <summary>Gets the branch with the specified name.</summary>
        /// <param name="name">The canonical or friendly name of the branch.</param>
        public Branch this[string name] =>
            GetBranch(name) ??
            throw new KeyNotFoundException($"Branch '{name}' not found.");

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public IEnumerator<Branch> GetEnumerator() => _branches.Values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}