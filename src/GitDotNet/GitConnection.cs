using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a Git repository.</summary>
public static class GitConnection
{
    /// <summary>
    /// Clones a Git repository from the specified URL to the target path.
    /// </summary>
    /// <param name="path">The local directory path where the repository will be cloned.</param>
    /// <param name="url">The URL of the remote repository to clone from.</param>
    /// <param name="options">Optional cloning configuration settings.</param>
    /// <exception cref="ArgumentException">Thrown when the path or URL is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the clone operation fails.</exception>
    public static void Clone(string path, string url, CloneOptions? options = null)
    {
        Directory.CreateDirectory(path);
        var argument = options?.IsBare ?? false ? "--bare " : string.Empty;
        argument += options?.BranchName != null ? $"--branch {options.BranchName} " : string.Empty;
        argument += options?.RecurseSubmodules ?? false ? $"----recurse-submodules " : string.Empty;
        GitCliCommand.Execute(path, $"clone {argument} {url} .");
    }

    /// <summary>
    /// Determines whether the specified path contains a valid Git repository.
    /// </summary>
    /// <param name="path">The directory path to validate.</param>
    /// <returns><see langword="true"/> if the path contains a valid Git repository; otherwise, <see langword="false"/>.</returns>
    public static bool IsValid(string path)
    {
        var fullPath = Path.GetFullPath(path).Replace('\\', '/');
        if (!Directory.Exists(fullPath))
        {
            return false;
        }
        var gitFolder = new GitCliCommand().GetAbsoluteGitPath(fullPath)?.Replace('\\', '/');
        return gitFolder != null &&
            (gitFolder.Equals(fullPath.ToString()) || // Bare repository
            gitFolder.Equals(fullPath + "/.git")); // Non-bare repository
    }

    /// <summary>
    /// Creates a new Git repository at the specified path.
    /// </summary>
    /// <param name="path">The directory path where the new repository will be created.</param>
    /// <param name="isBare">If <see langword="true"/>, creates a bare repository without a working directory; otherwise, creates a standard repository.</param>
    /// <exception cref="ArgumentException">Thrown when the path is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the repository creation fails.</exception>
    public static void Create(string path, bool isBare = false)
    {
        Directory.CreateDirectory(path);
        var argument = isBare ? "--bare" : string.Empty;
        GitCliCommand.Execute(path, $"init {argument}");
    }
}
