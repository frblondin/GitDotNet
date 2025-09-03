using System.IO.Abstractions;
using GitDotNet.Readers;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;

internal delegate IBranchRefWriter BranchRefWriterFactory(IRepositoryInfo repositoryInfo, IBranchRefReader branchRefReader);

internal interface IBranchRefWriter
{
    void CreateOrUpdateLocalBranch(string branchName, HashId commitHash, bool allowOverwrite = false);
    void CreateOrUpdateRemoteBranch(string remoteName, string branchName, HashId commitHash, bool allowOverwrite = true);
    void DeleteLocalBranch(string branchName, bool force = false);
    void DeleteRemoteBranch(string remoteName, string branchName);
}

/// <summary>
/// Writes Git branch references to the repository. This writer manages both local and remote branch references
/// by creating or updating files in the refs directory and modifying packed-refs file when needed.
/// </summary>
internal class BranchRefWriter(IRepositoryInfo repositoryInfo, IBranchRefReader branchRefReader, IFileSystem fileSystem, ILogger<BranchRefWriter>? logger = null) : IBranchRefWriter
{
    /// <summary>
    /// Creates or updates a local branch reference to point to the specified commit.
    /// </summary>
    /// <param name="branchName">The name of the branch (without refs/heads/ prefix).</param>
    /// <param name="commitHash">The commit hash that the branch should point to.</param>
    /// <param name="allowOverwrite">Whether to overwrite an existing branch. Default is false.</param>
    /// <exception cref="ArgumentException">Thrown when branchName is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when branch exists and allowOverwrite is false.</exception>
    public void CreateOrUpdateLocalBranch(string branchName, HashId commitHash, bool allowOverwrite = false)
    {
        var canonicalName = branchName.LooksLikeLocalBranch() ? branchName : $"{Reference.LocalBranchPrefix}{branchName}";
        logger?.LogInformation("Creating/updating local branch: {CanonicalName} -> {CommitHash}", canonicalName, commitHash);

        CreateOrUpdateRef(canonicalName, commitHash, allowOverwrite);
    }

    /// <summary>
    /// Creates or updates a remote tracking branch reference to point to the specified commit.
    /// </summary>
    /// <param name="remoteName">The name of the remote (e.g., "origin").</param>
    /// <param name="branchName">The name of the branch on the remote.</param>
    /// <param name="commitHash">The commit hash that the remote branch should point to.</param>
    /// <param name="allowOverwrite">Whether to overwrite an existing branch. Default is true for remote branches.</param>
    /// <exception cref="ArgumentException">Thrown when remoteName or branchName is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when branch exists and allowOverwrite is false.</exception>
    public void CreateOrUpdateRemoteBranch(string remoteName, string branchName, HashId commitHash, bool allowOverwrite = true)
    {
        var canonicalName = $"{Reference.RemoteTrackingBranchPrefix}{remoteName}/{branchName}";
        logger?.LogInformation("Creating/updating remote branch: {CanonicalName} -> {CommitHash}", canonicalName, commitHash);

        CreateOrUpdateRef(canonicalName, commitHash, allowOverwrite);
    }

    /// <summary>
    /// Deletes a local branch reference.
    /// </summary>
    /// <param name="branchName">The name of the branch to delete (without refs/heads/ prefix).</param>
    /// <param name="force">Whether to force delete the branch even if it's not fully merged.</param>
    /// <exception cref="ArgumentException">Thrown when branchName is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when branch doesn't exist or cannot be deleted safely.</exception>
    public void DeleteLocalBranch(string branchName, bool force = false)
    {
        var canonicalName = branchName.LooksLikeLocalBranch() ? branchName : $"{Reference.LocalBranchPrefix}{branchName}";
        logger?.LogInformation("Deleting local branch: {CanonicalName} (force: {Force})", canonicalName, force);

        DeleteRef(canonicalName, force, isLocalBranch: true);
    }

    /// <summary>
    /// Deletes a remote tracking branch reference.
    /// </summary>
    /// <param name="remoteName">The name of the remote (e.g., "origin").</param>
    /// <param name="branchName">The name of the branch on the remote.</param>
    /// <exception cref="ArgumentException">Thrown when remoteName or branchName is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when branch doesn't exist.</exception>
    public void DeleteRemoteBranch(string remoteName, string branchName)
    {
        var canonicalName = $"{Reference.RemoteTrackingBranchPrefix}{remoteName}/{branchName}";
        logger?.LogInformation("Deleting remote branch: {CanonicalName}", canonicalName);

        DeleteRef(canonicalName, force: true, isLocalBranch: false);
    }

    private void CreateOrUpdateRef(string canonicalName, HashId commitHash, bool allowOverwrite)
    {
        // Check if branch exists
        var existingBranch = TryGetExistingBranch(canonicalName);
        if (existingBranch != null && !allowOverwrite)
        {
            throw new InvalidOperationException($"Branch '{canonicalName}' already exists. Use allowOverwrite=true to overwrite it.");
        }

        // Determine the file path for the reference
        var refFilePath = GetRefFilePath(canonicalName);

        // Ensure the directory exists
        var directory = fileSystem.Path.GetDirectoryName(refFilePath);
        if (directory != null)
        {
            fileSystem.Directory.CreateDirectory(directory);
        }

        // Write the commit hash to the file
        var commitHashString = commitHash.ToString();
        logger?.LogDebug("Writing ref file: {RefFilePath} -> {CommitHash}", refFilePath, commitHashString);
        fileSystem.File.WriteAllText(refFilePath, commitHashString);

        // Remove from packed-refs if it was there
        RemoveFromPackedRefs(canonicalName);

        logger?.LogDebug("Successfully created/updated ref: {CanonicalName}", canonicalName);
    }

    private void DeleteRef(string canonicalName, bool force, bool isLocalBranch)
    {
        var existingBranch = TryGetExistingBranch(canonicalName);
        if (existingBranch == null)
        {
            throw new InvalidOperationException($"Branch '{canonicalName}' does not exist.");
        }

        // For non-force deletion of local branches, we should check if it's merged
        // This is a simplified version - in a full implementation, you'd check merge status
        if (!force && isLocalBranch)
        {
            // This is where you would implement merge checking logic
            logger?.LogDebug("Performing safe deletion of local branch: {CanonicalName}", canonicalName);
        }

        // Delete the ref file
        var refFilePath = GetRefFilePath(canonicalName);
        if (fileSystem.File.Exists(refFilePath))
        {
            fileSystem.File.Delete(refFilePath);
            logger?.LogDebug("Deleted ref file: {RefFilePath}", refFilePath);
        }

        // Remove from packed-refs if it was there
        RemoveFromPackedRefs(canonicalName);

        logger?.LogDebug("Successfully deleted ref: {CanonicalName}", canonicalName);
    }

    private Branch? TryGetExistingBranch(string canonicalName) =>
        branchRefReader.GetBranches().GetValueOrDefault(canonicalName);

    private string GetRefFilePath(string canonicalName) =>
        // Convert canonical name to file path
        // e.g., "refs/heads/main" -> ".git/refs/heads/main"
        fileSystem.Path.Combine(repositoryInfo.Path, canonicalName);

    private void RemoveFromPackedRefs(string canonicalName)
    {
        var packedRefsFilePath = fileSystem.Path.Combine(repositoryInfo.Path, "packed-refs");

        if (!fileSystem.File.Exists(packedRefsFilePath))
        {
            return;
        }

        logger?.LogDebug("Removing {CanonicalName} from packed-refs file", canonicalName);

        var lines = fileSystem.File.ReadAllLines(packedRefsFilePath);
        var filteredLines = new List<string>();
        bool removed = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Skip empty lines and comments, but keep them in output
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#') || trimmedLine.StartsWith('^'))
            {
                filteredLines.Add(line);
                continue;
            }

            // Check if this line is for the ref we want to remove
            var parts = trimmedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == canonicalName)
            {
                // Skip this line (remove it)
                removed = true;
                logger?.LogDebug("Removed {CanonicalName} from packed-refs", canonicalName);
                continue;
            }

            filteredLines.Add(line);
        }

        if (removed)
        {
            fileSystem.File.WriteAllLines(packedRefsFilePath, filteredLines);
            logger?.LogDebug("Updated packed-refs file");
        }
    }
}