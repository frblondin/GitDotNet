using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public class CompareCommits(GitConnectionProvider factory, IHostApplicationLifetime host) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        using var connection = factory.Invoke(path);
        var diffs = await connection.CompareAsync("HEAD~1", "HEAD");
        PrintDiffs(diffs);
        host.StopApplication();
    }

    private static void PrintDiffs(IList<Change> diffs)
    {
        foreach (var diff in diffs)
        {
            WriteLine(diff);
        }
    }
}
