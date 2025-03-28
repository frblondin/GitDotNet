using System.Diagnostics.CodeAnalysis;
using System.Text;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents an entry in the Git index.</summary>
public record IndexEntry
{
    private readonly IObjectResolver _objectResolver;
    private Entry? _entry;

    internal IndexEntry(DateTime lastMetadataChange, DateTime lastDataChange, IndexEntryType type, int unixPermissions, int fileSize, HashId id, string path, IObjectResolver objectResolver)
    {
        LastMetadataChange = lastMetadataChange;
        LastDataChange = lastDataChange;
        Type = type;
        UnixPermissions = unixPermissions;
        FileSize = fileSize;
        Id = id;
        Path = path;
        _objectResolver = objectResolver;
    }

    /// <summary>Gets the last time the file's metadata changed.</summary>
    public DateTime LastMetadataChange { get; }

    /// <summary>Gets the last time the file's data changed.</summary>
    public DateTime LastDataChange { get; }

    /// <summary>Gets the type of the index entry (e.g., regular file, symlink, gitlink).</summary>
    public IndexEntryType Type { get; }

    /// <summary>Gets the Unix permissions of the file.</summary>
    public int UnixPermissions { get; }

    /// <summary>Gets the size of the file.</summary>
    public int FileSize { get; }

    /// <summary>Gets the hash of the file.</summary>
    public HashId Id { get; }

    /// <summary>Gets the path of the file.</summary>
    public GitPath Path { get; }

    /// <summary>Retrieves an entry asynchronously./// </summary>
    /// <typeparam name="TEntry">Represents the type of entry being retrieved.</typeparam>
    /// <returns>Returns the entry of the specified type.</returns>
    public async Task<TEntry> GetEntryAsync<TEntry>() where TEntry : Entry => (TEntry)(_entry ??= await _objectResolver.GetAsync<TEntry>(Id));

    /// <summary>Prints the members of the <see cref="IndexEntry"/> to the provided <see cref="StringBuilder"/>.</summary>
    /// <param name="builder">The <see cref="StringBuilder"/> to append the member information to.</param>
    /// <returns>Always returns <c>true</c>.</returns>
    [ExcludeFromCodeCoverage]
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append(Path).Append(" { ");
        builder.Append("Hash = ").Append(Id.ToString()).Append(", ");
        builder.Append("Type = ").Append(Type).Append(", ");
        builder.Append("FileSize = ").Append(FileSize).Append(", ");
        builder.Append("LastDataChange = ").Append(LastDataChange).Append(" } ");
        return true;
    }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override string ToString()
    {
        var result = new StringBuilder();
        PrintMembers(result);
        return result.ToString();
    }
}

/// <summary>Specifies the type of an index entry.</summary>
public enum IndexEntryType
{
    /// <summary>A regular file.</summary>
    Regular = 8,

    /// <summary>A symbolic link.</summary>
    Symlink = 10,

    /// <summary>A gitlink (submodule).</summary>
    GitLink = 14,
}
