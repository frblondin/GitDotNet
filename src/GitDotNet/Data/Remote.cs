using System;
using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml.Linq;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a Git branch.</summary>
[ExcludeFromCodeCoverage]
public class Remote
{
    private readonly IRepositoryInfo _info;

    internal Remote(string name, IRepositoryInfo info)
    {
        Name = name;
        _info = info;
    }

    /// <summary>Gets the full name of the branch.</summary>
    public string Name { get; }

    /// <summary>Gets the URL of the remote.</summary>
    public string? Url => _info.Config.GetNamedSection("remote", Name)!["url"];

    /// <summary>Gets the refspec of the remote.</summary>
    public string? Refspec => _info.Config.GetNamedSection("remote", Name)!["fetch"];

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Fetches updates from a remote repository using specified options.</summary>
    /// <param name="options">Allows customization of the fetch operation, such as setting the depth of the fetch.</param>
    [ExcludeFromCodeCoverage]
    public void Fetch(FetchOptions? options = null)
    {
        var arguments = (options?.Depth ?? -1) > -1 ? $"--depth={options!.Depth}" : "";
        GitCliCommand.Execute(_info.Path, $"fetch {arguments} {Name}");
    }

    /// <summary>Represents a collection of Git branches.</summary>
    [DebuggerDisplay("Count = {Count}")]
    public class List : IReadOnlyCollection<Remote>
    {
        private readonly IRepositoryInfo _info;
        private readonly IDictionary<string, Remote> _remotes;

        internal List(IRepositoryInfo info, IDictionary<string, Remote> remotes)
        {
            _info = info;
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

        /// <summary>Adds a new remote repository with a specified name and URL.</summary>
        /// <param name="name">Specifies the identifier for the remote repository being added.</param>
        /// <param name="url">Defines the location of the remote repository to be linked.</param>
        public Remote Add(string name, string url)
        {
            name = name.Trim();
            GitCliCommand.Execute(_info.Path, $"remote add {name} {url}");
            return _remotes[name] = new(name, _info);
        }
    }
}