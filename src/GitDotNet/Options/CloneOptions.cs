namespace GitDotNet;

/// <summary>Represents options for cloning repositories.</summary>
/// <param name="IsBare">Indicates whether the repository should be cloned as a bare repository.</param>
/// <param name="BranchName">The name of the branch to checkout. When unspecified the remote's default branch will be used instead.</param>
/// <param name="RecurseSubmodules">Recursively clone submodules.</param>
public sealed record class CloneOptions(bool IsBare = false, string? BranchName = null, bool RecurseSubmodules = false);