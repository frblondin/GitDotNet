namespace GitDotNet;

/// <summary>Specifies the type of a Git entry.</summary>
public enum EntryType
{
    /// <summary>A commit entry.</summary>
    Commit = 1,

    /// <summary>A tree entry.</summary>
    Tree,

    /// <summary>A blob entry.</summary>
    Blob,

    /// <summary>A tag entry.</summary>
    Tag,

    /// <summary>An offset delta entry.</summary>
    OfsDelta = 6,

    /// <summary>A reference delta entry.</summary>
    RefDelta,

    /// <summary>Represents a single entry in a log.</summary>
    LogEntry,
}
