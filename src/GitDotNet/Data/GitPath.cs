using System.Diagnostics.CodeAnalysis;

namespace GitDotNet;

/// <summary>Represents a Git path composed of multiple path chunks.</summary>
public sealed class GitPath : IEquatable<GitPath>, IComparable<GitPath>
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

    /// <summary>Gets the path chunk at the specified index.</summary>
    public string Root => _pathChunks[0];

    /// <summary>Gets the path chunk at the specified index.</summary>
    public int Length => _pathChunks.Length;

    /// <summary>Gets the path chunk of child.</summary>
    public GitPath ChildPath => new(_pathChunks[1..]);

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

    /// <summary>Implicitly converts a string array to a <see cref="GitPath"/>.</summary>
    /// <param name="pathChunks">The array of path chunks.</param>
    public static implicit operator GitPath(string[] pathChunks) => new(pathChunks);
}
