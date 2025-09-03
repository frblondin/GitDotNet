using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal delegate IBranchRefReader BranchRefReaderFactory(IGitConnection connection);

internal interface IBranchRefReader
{
    IImmutableDictionary<string, Branch> GetBranches();
}

internal partial class BranchRefReader(IGitConnection connection,
    IFileSystem fileSystem, ILogger<BranchRefReader>? logger = null) : IBranchRefReader
{
    public IImmutableDictionary<string, Branch> GetBranches()
    {
        logger?.LogInformation("Getting branches for repository: {Path}", connection.Info.Path);
        var branches = new SortedSet<Branch>();
        GetBranchesFromRefsDirectory(branches);
        GetBranchesFromPackedRefsFile(branches);
        logger?.LogDebug("Found {BranchCount} branches.", branches.Count);
        return branches.ToImmutableDictionary(b => b.CanonicalName);
    }

    private void GetBranchesFromPackedRefsFile(SortedSet<Branch> branches)
    {
        var refsFilePath = Path.Combine(connection.Info.Path, "packed-refs");

        if (fileSystem.File.Exists(refsFilePath))
        {
            logger?.LogDebug("Reading packed-refs file: {RefsFilePath}", refsFilePath);
            var lines = fileSystem.File.ReadAllLines(refsFilePath);

            foreach (var line in lines)
            {
                var match = GitRefHeadOrRemoteRegex().Match(line.Trim());
                if (match.Success)
                {
                    var hash = new HashId(match.Groups[1].Value);
                    var canonicalName = match.Groups[2].Value;
                    var branch = new Branch(canonicalName, connection, () => hash);
                    branches.Add(branch);
                    logger?.LogDebug("Added branch from packed-refs: {CanonicalName}", canonicalName);
                }
            }
        }
    }

    private void GetBranchesFromRefsDirectory(SortedSet<Branch> branches)
    {
        var refsPath = Path.Combine(connection.Info.Path, "refs");

        if (!fileSystem.Directory.Exists(refsPath))
        {
            logger?.LogWarning("Refs directory does not exist: {RefsPath}", refsPath);
            return;
        }
        var localBranchesPath = Path.Combine(refsPath, "heads");
        var remoteBranchesPath = Path.Combine(refsPath, "remotes");

        if (fileSystem.Directory.Exists(localBranchesPath))
        {
            var localBranches = from path in fileSystem.Directory.GetFiles(localBranchesPath, "*", SearchOption.AllDirectories)
                                let name = fileSystem.Path.GetRelativePath(localBranchesPath, path).Replace(Path.DirectorySeparatorChar, '/')
                                let fullName = $"{Reference.LocalBranchPrefix}{name}"
                                select new Branch(fullName, connection, () => ReadTip(path));
            foreach (var branch in localBranches)
            {
                branches.Add(branch);
                logger?.LogDebug("Added local branch: {CanonicalName}", branch.CanonicalName);
            }
        }

        if (fileSystem.Directory.Exists(remoteBranchesPath))
        {
            var remoteBranches = from path in fileSystem.Directory.GetFiles(remoteBranchesPath, "*", SearchOption.AllDirectories)
                                 let name = fileSystem.Path.GetRelativePath(remoteBranchesPath, path).Replace(Path.DirectorySeparatorChar, '/')
                                 let fullName = $"{Reference.RemoteTrackingBranchPrefix}{name}"
                                 select new Branch(fullName, connection, () => ReadTip(path));
            foreach (var branch in remoteBranches)
            {
                branches.Add(branch);
                logger?.LogDebug("Added remote branch: {CanonicalName}", branch.CanonicalName);
            }
        }

        HashId ReadTip(string file)
        {
            var content = fileSystem.File.ReadAllText(file);
            logger?.LogDebug("Read tip for branch file: {File}", file);
            return new HashId(content.Trim('.', '\r', '\n'));
        }
    }

    [GeneratedRegex(@"^([a-f0-9]{40})\s+(refs/(heads|remotes)/.+)$", RegexOptions.Compiled)]
    internal static partial Regex GitRefHeadOrRemoteRegex();
}
