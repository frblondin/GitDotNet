using GitDotNet.Tools;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace GitDotNet;

/// <summary>Represents a base class for different types of Git entries.</summary>
public abstract record class Entry : IEquatable<Entry>
{
    /// <summary>The data of the Git entry.</summary>
    protected internal virtual byte[] Data { get; }

    internal Entry(EntryType type, HashId id, byte[] data)
    {
        Type = type;
        Id = id;
        Data = data;
    }

    /// <summary>Gets the type of the Git entry.</summary>
    public EntryType Type { get; }

    /// <summary>Gets the hash of the Git entry.</summary>
    public HashId Id { get; init; }

    /// <inheritdoc/>>
    [ExcludeFromCodeCoverage]
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Hash = ").Append(Id.ToString());
        return true;
    }

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