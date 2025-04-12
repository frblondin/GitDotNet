using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace GitDotNet;

/// <summary>Represents a base class for different types of Git entries.</summary>
[DebuggerDisplay("{Id,nq}")]
public abstract class Entry : IEquatable<Entry>
{
    internal Entry(EntryType type, HashId id, byte[] data)
    {
        Type = type;
        Id = id;
        Data = data;
    }

    /// <summary>The data of the Git entry.</summary>
    protected internal virtual byte[] Data { get; }

    /// <summary>Gets the type of the Git entry.</summary>
    public EntryType Type { get; }

    /// <summary>Gets the hash of the Git entry.</summary>
    public HashId Id { get; internal set; }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(GetType().Name);
        builder.Append(" { ");
        if (PrintMembers(builder))
        {
            builder.Append(' ');
        }
        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>
    /// Appends the string representation of the object's hash to a StringBuilder instance.
    /// </summary>
    /// <param name="builder">Used to construct a string that includes the object's hash value.</param>
    /// <returns>Always returns true after appending the hash information.</returns>
    [ExcludeFromCodeCoverage]
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Hash = ").Append(Id.ToString());
        return true;
    }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override bool Equals(object? obj) => Equals(obj as Entry);

    /// <inheritdoc/>
    public virtual bool Equals(Entry? other) => other is not null && Id.Equals(other.Id);

    /// <inheritdoc/>
    public override int GetHashCode() => GetHashCode(Id.Hash);

    private static int GetHashCode(IEnumerable<byte> array)
    {
        unchecked
        {
            int hash = 17;

            // Cycle through each element in the array.
            foreach (var value in array)
            {
                // Update the hash.
                hash = hash * 23 + value.GetHashCode();
            }

            return hash;
        }
    }
}

internal record class UnlinkedEntry(EntryType Type, HashId Id, byte[] Data);