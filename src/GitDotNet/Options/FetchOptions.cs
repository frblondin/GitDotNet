namespace GitDotNet;

/// <summary>Represents options for fetching changes from a remote repository.</summary>
/// <param name="Depth">Limit fetching to the specified number of commits from the tip of each remote branch history.</param>
public sealed record class FetchOptions(int Depth = -1);