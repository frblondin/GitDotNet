using System.Diagnostics.CodeAnalysis;

namespace GitDotNet;

/// <summary>Represents a change between two Git tree entries.</summary>
/// <param name="Type">The type of change.</param>
/// <param name="OldPath">The old path of tree entry item.</param>
/// <param name="NewPath">The new path of tree entry item.</param>
/// <param name="Old">The old tree entry item.</param>
/// <param name="New">The new tree entry item.</param>
public record class Change(ChangeType Type, GitPath? OldPath, GitPath? NewPath, TreeEntryItem? Old, TreeEntryItem? New)
{
    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{Type}: {OldPath?.ToString() ?? "null"} -> {NewPath?.ToString() ?? "null"}";
}
