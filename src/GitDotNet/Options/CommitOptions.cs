namespace GitDotNet;

/// <summary>
/// Represents options for committing changes in a version control system.
/// </summary>
/// <param name="AmendPreviousCommit">Indicates whether the previous commit should be modified instead of creating a new one.</param>
/// <param name="AllowEmpty">
/// Usually recording a commit that has the exact same tree as its sole parent commit is a mistake,
/// and the command prevents you from making such a commit.
/// This option bypasses the safety, and is primarily for use by foreign SCM interface scripts.
/// </param>
/// <param name="UpdateBranch">true to update the branch reference; otherwise, false.</param>
/// <param name="UpdateHead">true to update the HEAD reference; otherwise, false.</param>
public sealed record class CommitOptions(bool AmendPreviousCommit = false, bool AllowEmpty = false, bool UpdateBranch = true, bool UpdateHead = true);