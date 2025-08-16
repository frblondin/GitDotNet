using GitDotNet.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static System.Console;
using static GitDotNet.Console.Tools.InfoInput;

namespace GitDotNet.Console;

public class ShowStash(GitConnectionProvider factory, IHostApplicationLifetime host) : BackgroundService
{
    public const bool ShowBlobContent = false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        using var connection = factory.Invoke(path);
        var stashes = await connection.GetStashesAsync();
        if (stashes.Count == 0)
        {
            WriteLine("No stash found.");
            host.StopApplication();
            return;
        }
        var stash = GetStashToShow(stashes);
        using var stream = OpenStandardOutput();

        WriteLine();
        WriteLine("Staged changes:");
        await new GitPatchCreator().WriteAsync(stream,
            await stash.GetHeadAsync(),
            await stash.GetStagedCommitAsync(),
            await stash.GetStagedChangesAsync());

        WriteLine();
        WriteLine("Unstaged changes:");
        await new GitPatchCreator().WriteAsync(stream,
            await stash.GetUntrackedCommitAsync() ?? await stash.GetStagedCommitAsync(),
            stash,
            [.. await stash.GetUnStagedChangesAsync(), .. await stash.GetUntrackedChangesAsync()]);

        host.StopApplication();
    }

    private static Stash GetStashToShow(IReadOnlyList<Stash> stashes)
    {
        WriteLine("Found {0} stash(es):", stashes.Count);
        var i = 1;
        foreach (var stash in stashes)
        {
            WriteLine($"   {i++}. {stash.Id} - {stash.Message}");
        }
        var index = int.Parse(InputData($"Enter stash index to show (1 to {stashes.Count}):",
            x => int.TryParse(x, out var index) && index >= 1 && index <= stashes.Count,
            "1"));
        return stashes[index - 1];
    }
}
