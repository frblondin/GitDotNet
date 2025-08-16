using System.IO.Abstractions;

namespace GitDotNet.Readers;

internal delegate StashRefReader StashRefReaderFactory(GitConnection connection);

/// <summary>Reads stash references from .git/logs/refs/stash file and returns Stash instances.</summary>
internal class StashRefReader(GitConnection connection, IFileSystem fileSystem)
{
    private readonly string _reflogPath = fileSystem.Path.Combine(connection.Info.Path, "logs", "refs", "stash");

    /// <summary>
    /// Gets all Stash entries from the reflog file.
    /// </summary>
    public async Task<IReadOnlyList<Stash>> GetStashesAsync()
    {
        var stashes = new List<Stash>();
        if (!fileSystem.File.Exists(_reflogPath))
            return stashes;

        var lines = await fileSystem.File.ReadAllLinesAsync(_reflogPath).ConfigureAwait(false);
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 2)
                continue;
            var sha = parts[1];
            if (HashId.TryParse(sha, out var stashId))
            {
                var commit = await connection.Objects.GetAsync<CommitEntry>(stashId).ConfigureAwait(false);
                stashes.Add(new(commit, connection));
            }
        }
        return stashes;
    }
}
