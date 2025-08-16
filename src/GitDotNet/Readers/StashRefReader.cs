using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal delegate StashRefReader StashRefReaderFactory(GitConnection connection);

/// <summary>Reads stash references from .git/logs/refs/stash file and returns Stash instances.</summary>
internal class StashRefReader(GitConnection connection, IFileSystem fileSystem, ILogger<StashRefReader>? logger = null)
{
    private readonly string _reflogPath = fileSystem.Path.Combine(connection.Info.Path, "logs", "refs", "stash");

    /// <summary>Gets all Stash entries from the reflog file.</summary>
    public async Task<IReadOnlyList<Stash>> GetStashesAsync()
    {
        var stashes = new List<Stash>();
        if (!fileSystem.File.Exists(_reflogPath))
        {
            logger?.LogInformation("Stash reflog file not found at {Path}", _reflogPath);
            return stashes;
        }

        var lines = await fileSystem.File.ReadAllLinesAsync(_reflogPath).ConfigureAwait(false);
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 2)
            {
                logger?.LogDebug("Skipping malformed stash reflog line: {Line}", line);
                continue;
            }
            var sha = parts[1];
            if (HashId.TryParse(sha, out var stashId))
            {
                var commit = await connection.Objects.GetAsync<CommitEntry>(stashId).ConfigureAwait(false);
                stashes.Add(new(commit, connection));
            }
            else
            {
                logger?.LogWarning("Unable to parse stash SHA: {Sha} in line: {Line}", sha, line);
            }
        }
        logger?.LogInformation("Read {Count} stash entries from {Path}", stashes.Count, _reflogPath);
        return stashes;
    }
}
