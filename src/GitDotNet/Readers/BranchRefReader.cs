using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using GitDotNet.Tools;

namespace GitDotNet.Readers;

internal delegate BranchRefReader BranchRefReaderFactory(GitConnection connection);

internal partial class BranchRefReader(GitConnection connection, IFileSystem fileSystem)
{
    internal Branch.List GetBranches()
    {
        var branches = new SortedSet<Branch>();
        GetBranchesFromPackedRefsFile(branches);
        GetBranchesFromRefsDirectory(branches);
        return new Branch.List(branches.ToImmutableDictionary(b => b.CanonicalName));
    }

    private void GetBranchesFromPackedRefsFile(SortedSet<Branch> branches)
    {
        var refsFilePath = Path.Combine(connection.Info.Path, "packed-refs");

        if (fileSystem.File.Exists(refsFilePath))
        {
            var lines = fileSystem.File.ReadAllLines(refsFilePath);

            foreach (var line in lines)
            {
                var match = GitRefHeadOrRemoteRegex().Match(line.Trim());
                if (match.Success)
                {
                    var hash = new HashId(match.Groups[1].Value);
                    var canonicalName = match.Groups[2].Value;
                    var branch = new Branch(canonicalName, connection, async () => await connection.Objects.GetAsync<CommitEntry>(hash));
                    branches.Add(branch);
                }
            }
        }
    }

    private void GetBranchesFromRefsDirectory(SortedSet<Branch> branches)
    {
        var refsPath = Path.Combine(connection.Info.Path, "refs");

        if (!fileSystem.Directory.Exists(refsPath))
        {
            return;
        }
        var localBranchesPath = Path.Combine(refsPath, "heads");
        var remoteBranchesPath = Path.Combine(refsPath, "remotes");

        if (fileSystem.Directory.Exists(localBranchesPath))
        {
            var localBranches = from path in fileSystem.Directory.GetFiles(localBranchesPath, "*", SearchOption.AllDirectories)
                                let name = fileSystem.Path.GetRelativePath(localBranchesPath, path).Replace(Path.DirectorySeparatorChar, '/')
                                let fullName = $"{Reference.LocalBranchPrefix}{name}"
                                select new Branch(fullName, connection, async () => await ReadTipAsync(path));
            foreach (var branch in localBranches)
            {
                branches.Add(branch);
            }
        }

        if (fileSystem.Directory.Exists(remoteBranchesPath))
        {
            var remoteBranches = from path in fileSystem.Directory.GetFiles(remoteBranchesPath, "*", SearchOption.AllDirectories)
                                 let name = fileSystem.Path.GetRelativePath(remoteBranchesPath, path).Replace(Path.DirectorySeparatorChar, '/')
                                 let fullName = $"{Reference.RemoteTrackingBranchPrefix}{name}"
                                 select new Branch(fullName, connection, async () => await ReadTipAsync(path));
            foreach (var branch in remoteBranches)
            {
                branches.Add(branch);
            }
        }

        async Task<CommitEntry> ReadTipAsync(string file)
        {
            var content = fileSystem.File.ReadAllText(file);
            var id = new HashId(content.Trim('.', '\r', '\n'));
            return await connection.Objects.GetAsync<CommitEntry>(id);
        }
    }

    [GeneratedRegex(@"^([a-f0-9]{40})\s+(refs/(heads|remotes)/.+)$", RegexOptions.Compiled)]
    internal static partial Regex GitRefHeadOrRemoteRegex();
}
