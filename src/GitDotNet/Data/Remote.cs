using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using GitDotNet.Tools;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace GitDotNet;

/// <summary>Represents a Git branch.</summary>
[ExcludeFromCodeCoverage]
public class Remote
{
    private readonly GitConnection _gitConnection;

    internal Remote(string name, GitConnection gitConnection)
    {
        Name = name;
        _gitConnection = gitConnection;
    }

    /// <summary>Gets the full name of the branch.</summary>
    public string Name { get; }

    /// <summary>Gets the URL of the remote.</summary>
    public string? Url => _gitConnection.Info.Config.GetNamedSection("remote", Name)!["url"];

    /// <summary>Gets the refspec of the remote.</summary>
    public string? Refspec => _gitConnection.Info.Config.GetNamedSection("remote", Name)!["fetch"];

    /// <summary>
    /// Fetch branches and/or tags (collectively, "refs") from one or more other repositories, along with the objects
    /// necessary to complete their histories. Remote-tracking branches are updated.
    /// </summary>
    /// <remarks>
    /// By default, any tag that points into the histories being fetched is also fetched; the effect is to fetch tags
    /// that point at branches that you are interested in. This default behavior can be changed by using the --tags or
    /// --no-tags options or by configuring remote.[name].tagOpt.By using a refspec that fetches tags explicitly, you
    /// can fetch tags that do not point into branches you are interested in as well.
    /// </remarks>
    /// <param name="depth">Limit fetching to the specified number of commits from the tip of each remote branch history.</param>
    /// <param name="deepen">
    /// Similar to <paramref name="depth"/>, except it specifies the number of commits from the current shallow boundary
    /// instead of from the tip of each remote branch history.
    /// </param>
    public void Fetch(int? depth = null, int? deepen = null)
    {
        var options = new StringBuilder();
        if (depth.HasValue)
        {
            options.Append($"--depth {depth}");
        }
        if (deepen.HasValue)
        {
            options.Append($"--deepen {deepen}");
        }
        GitCliCommand.Execute(_gitConnection.Info.Path, $"fetch {Name} {options}");
    }

/// <inheritdoc/>
    public override string ToString() => Name;

/// <summary>Represents a collection of Git branches.</summary>
    [DebuggerDisplay("Count = {Count}")]
public class List : IReadOnlyCollection<Remote>
{
    private readonly IImmutableDictionary<string, Remote> _remotes;
    internal List(IImmutableDictionary<string, Remote> remotes)
    {
        _remotes = remotes;
    }

    /// <summary>Gets the remote with the specified name.</summary>
    /// <param name="name">The name of the remote.</param>
        public Remote this[string name] => GetRemote(name);

        /// <inheritdoc/>
        public int Count => _remotes.Count;

        private Remote GetRemote(string name)
        {
            if (_remotes.TryGetValue(name, out var remote) ||
                _remotes.TryGetValue($"refs/heads/{name}", out remote) ||
                _remotes.TryGetValue($"refs/remotes/{name}", out remote))
            {
                return remote;
            }
            throw new KeyNotFoundException($"Branch '{name}' not found.");
        }

        /// <inheritdoc/>
        public IEnumerator<Remote> GetEnumerator() => _remotes.Values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}