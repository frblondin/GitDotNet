using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a hash identifier.</summary>
#if NET8_0
public partial class HashId : IEquatable<HashId>, IComparable<HashId>
#else
public partial class HashId : IEquatable<HashId>, IComparable<HashId>, IComparable<Span<byte>>
#endif
{
    /// <summary>Gets an empty <see cref="HashId"/>.</summary>
    public static HashId Empty { get; } = new([]);

    private readonly byte[] _hash;
    private string? _hashString;

    /// <summary>Initializes a new instance of the <see cref="HashId"/> class.</summary>
    /// <param name="hash">The hash value.</param>
    public HashId(byte[] hash)
    {
        if (hash.Length < 4 && hash.Length > 0)
        {
            throw new ArgumentException("The hash must be at least 4 bytes long.", nameof(hash));
        }
        _hash = hash;
        Hash = _hash.AsReadOnly();
    }

    /// <summary>Initializes a new instance of the <see cref="HashId"/> class.</summary>
    /// <param name="hash">The hash value.</param>
    public HashId(string hash) : this(hash.HexToByteArray()) { }

    /// <summary>Gets the hash value.</summary>
    public IReadOnlyList<byte> Hash { get; }

    /// <summary>Returns the hash code for this instance.</summary>
    /// <returns>The hash code for this instance.</returns>
    public override int GetHashCode() => BitConverter.ToInt32(_hash.AsSpan(0, 4));

    /// <summary>Converts a byte array to a <see cref="HashId"/>.</summary>
    /// <param name="hash">The byte array.</param>
    public static implicit operator HashId(byte[] hash) => new(hash);

    /// <summary>Tries to parse the specified string as a <see cref="HashId"/>.</summary>
    /// <param name="hash">The string to parse.</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="HashId"/> if the parse succeeded, or null if the parse failed.</param>
    /// <returns>true if the string was parsed successfully; otherwise, false.</returns>
    public static bool TryParse(string hash, [NotNullWhen(true)] out HashId? result)
    {
        if (Sha1Pattern().IsMatch(hash) || Sha256Pattern().IsMatch(hash))
        {
            result = new(hash.HexToByteArray());
            return true;
        }
        result = null;
        return false;
    }

    /// <inheritdoc/>
    public int CompareTo(HashId? other)
    {
        if (other is null) return 1;
        if (_hash.Length == 0 || other._hash.Length == 0) return _hash.Length.CompareTo(other._hash.Length);
        for (var i = 0; i < Math.Min(_hash.Length, other._hash.Length); i++)
        {
            var comparison = _hash[i].CompareTo(other._hash[i]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    /// <inheritdoc/>
    public int CompareTo(Span<byte> other)
    {
        if (_hash.Length == 0 || other.Length == 0) return _hash.Length.CompareTo(other.Length);
        for (var i = 0; i < Math.Min(_hash.Length, other.Length); i++)
        {
            var comparison = _hash[i].CompareTo(other[i]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    /// <inheritdoc/>
    public bool Equals(HashId? other)
    {
        if (other is null) return false;
        if (_hash.Length == 0 || other._hash.Length == 0) return _hash.Length == other._hash.Length;
        return _hash.Take(Math.Min(_hash.Length, other._hash.Length)).SequenceEqual(other._hash);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as HashId);

    /// <inheritdoc/>
    public override string ToString() => _hashString ??= ByteArrayToHex();

    private string ByteArrayToHex()
    {
        var hex = new StringBuilder(_hash.Length * 2);
        foreach (var b in _hash)
        {
            hex.AppendFormat("{0:x2}", b);
        }
        return hex.ToString();
    }

    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.Compiled)]
    private static partial Regex Sha1Pattern();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256Pattern();
}