namespace GitDotNet;

/// <summary>Represents options for logging commits in a Git repository.</summary>
/// <param name="SortBy">Specifies the traversal method for sorting commits.</param>
/// <param name="LastReferences">The last references to include in the log.</param>
/// <param name="Start">The start time for filtering commits.</param>
/// <param name="End">The end time for filtering commits.</param>
public record class LogOptions(LogTraversal SortBy,
                               IList<string>? LastReferences = null,
                               DateTimeOffset? Start = null,
                               DateTimeOffset? End = null)
{
    /// <summary>Gets the default log options.</summary>
    public static LogOptions Default { get; } = new(LogTraversal.Time);
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

    /// <summary>Sort commits in reverse order.</summary>
    Reverse = 1 << 3,
}
