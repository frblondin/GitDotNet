namespace GitDotNet;

/// <summary>Specifies the type of change between two Git tree entries.</summary>
public enum ChangeType
{
    /// <summary>Indicates that an item was added.</summary>
    Added,

    /// <summary>Indicates that an item was removed.</summary>
    Removed,

    /// <summary>Indicates that an item was modified.</summary>
    Modified,

    /// <summary>Indicates that an item was renamed or moved.</summary>
    Renamed
}