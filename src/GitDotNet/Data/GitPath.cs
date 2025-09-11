using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace GitDotNet;

/// <summary>Represents a Git path composed of multiple path chunks.</summary>
public sealed class GitPath : IEquatable<GitPath>, IComparable<GitPath>, IEnumerable<string>
{
    private readonly string[] _pathChunks;
    private string? _pathString;

    /// <summary>Initializes a new instance of the <see cref="GitPath"/> class.</summary>
    /// <param name="pathChunks">The array of path chunks.</param>
    public GitPath(params string[] pathChunks)
    {
        _pathChunks = pathChunks ?? throw new ArgumentNullException(nameof(pathChunks));
    }

    /// <summary>Initializes a new instance of the <see cref="GitPath"/> class.</summary>
    /// <param name="path">The string path.</param>
    public GitPath(string path)
    {
        _pathChunks = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Gets an empty <see cref="GitPath"/>.</summary>
    public static GitPath Empty { get; } = new();

    /// <summary>Gets whether the current path is empty.</summary>
    public bool IsEmpty => _pathChunks.Length == 0;

    /// <summary>Gets the path chunk at the specified index.</summary>
    public string Root => _pathChunks[0];

    /// <summary>Gets the name of current path element.</summary>
    public string Name => _pathChunks[^1];

    /// <summary>Gets the parent path derived by removing the last segment from the current path.</summary>
    public GitPath Parent => new(_pathChunks[..^1]);

    /// <summary>Gets the path chunk at the specified index.</summary>
    public int Length => _pathChunks.Length;

    /// <summary>Gets the path chunk of child.</summary>
    public GitPath ChildPath => new(_pathChunks[1..]);

    /// <summary>Creates a new <see cref="GitPath"/> by appending a child segment to the current path.</summary>
    public GitPath AddChild(string name) => new([.. _pathChunks, name]);

    /// <summary>Gets whether current instance contains <paramref name="other"/>.</summary>
    /// <param name="other">The path to be verified.</param>
    public bool Contains(GitPath other) =>
        other._pathChunks.Length >= _pathChunks.Length &&
        other._pathChunks.Take(_pathChunks.Length).SequenceEqual(_pathChunks);

    /// <inheritdoc/>
    public bool Equals(GitPath? other)
    {
        if (other is null) return false;
        return _pathChunks.SequenceEqual(other._pathChunks);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as GitPath);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            foreach (var chunk in _pathChunks)
            {
                hash = hash * 23 + (chunk?.GetHashCode() ?? 0);
            }
            return hash;
        }
    }

    /// <inheritdoc/>
    public int CompareTo(GitPath? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < Math.Min(_pathChunks.Length, other._pathChunks.Length); i++)
        {
            int comparison = string.Compare(_pathChunks[i], other._pathChunks[i], StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }
        return _pathChunks.Length.CompareTo(other._pathChunks.Length);
    }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator ==(GitPath? left, GitPath? right) => Equals(left, right);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator !=(GitPath? left, GitPath? right) => !Equals(left, right);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator <(GitPath? left, GitPath? right) => left is not null && right is not null && left.CompareTo(right) < 0;

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator <=(GitPath? left, GitPath? right) => left is not null && right is not null && left.CompareTo(right) <= 0;

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator >(GitPath? left, GitPath? right) => left is not null && right is not null && left.CompareTo(right) > 0;

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public static bool operator >=(GitPath? left, GitPath? right) => left is not null && right is not null && left.CompareTo(right) >= 0;

    /// <summary>Converts a string to a <see cref="GitPath"/>.</summary>
    /// <param name="path">The string path.</param>
    public static implicit operator GitPath(string path) => new(path);

    /// <inheritdoc/>
    public override string ToString() => _pathString ??= string.Join('/', _pathChunks);

    internal void AppendTo(StreamWriter writer)
    {
        var first = true;
        foreach (var chunk in _pathChunks)
        {
            if (!first) writer.Write('/');
            writer.Write(chunk);
            first = false;
        }
    }

    /// <inheritdoc/>
    public IEnumerator<string> GetEnumerator() => _pathChunks.AsEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Implicitly converts a string array to a <see cref="GitPath"/>.</summary>
    /// <param name="pathChunks">The array of path chunks.</param>
    public static implicit operator GitPath(string[] pathChunks) => new(pathChunks);
}
