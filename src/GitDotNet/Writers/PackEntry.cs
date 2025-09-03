namespace GitDotNet.Writers;

/// <summary>Represents an entry to be written to a pack file.</summary>
/// <param name="Type">The entry type.</param>
/// <param name="Id">The unique identifier of the entry.</param>
/// <param name="Data">The entry data.</param>
/// <param name="BaseId">The base object ID for delta entries (null for non-delta entries).</param>
internal record PackEntry(EntryType Type, HashId Id, byte[] Data, HashId? BaseId = null)
{
    /// <summary>Gets a value indicating whether this entry is a delta entry.</summary>
    public bool IsDelta => Type is EntryType.RefDelta or EntryType.OfsDelta;
}

/// <summary>Represents delta match information for pack optimization.</summary>
internal record DeltaMatch(int BaseOffset, int TargetOffset, int Length, int Score);