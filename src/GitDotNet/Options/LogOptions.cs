namespace GitDotNet;

/// <summary>Represents options for logging commits in a Git repository.</summary>
/// <param name="SortBy">Specifies the traversal method for sorting commits.</param>
/// <param name="ExcludeReachableFrom">The reference that should make the traversal stop.</param>
/// <param name="Start">The start time for filtering commits.</param>
/// <param name="End">The end time for filtering commits.</param>
/// <param name="Path">Show only commits that are enough to explain how the specified blob path came to be</param>
public record class LogOptions(LogTraversal SortBy,
                               string? ExcludeReachableFrom = null,
                               DateTimeOffset? Start = null,
                               DateTimeOffset? End = null,
                               GitPath? Path = null)
{
    /// <summary>Gets the default log options.</summary>
    public static LogOptions Default { get; } = new(LogTraversal.FirstParentOnly);
}

/// <summary>Specifies the traversal method for sorting commits in a Git log.</summary>
[Flags]
public enum LogTraversal
{
    /// <summary>Do not sort commits.</summary>
    None = 0,

    /// <summary>Follow only the first parent of each commit.</summary>
    FirstParentOnly = 1 << 0,

    /// <summary>Sort commits by commit time.</summary>
    Time = 1 << 1,

    /// <summary>Sort commits topologically, meaning parents before children.</summary>
    Topological = 1 << 2,
}
