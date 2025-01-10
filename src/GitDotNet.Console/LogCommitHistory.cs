using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public class LogCommitHistory(GitConnectionProvider factory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        using var connection = factory.Invoke(path);
        await foreach (var commit in connection.GetLogAsync("HEAD",
            LogOptions.Default with { SortBy = LogTraversal.FirstParentOnly | LogTraversal.Topological }))
        {
            if (stoppingToken.IsCancellationRequested) break;
            var messagePreview = commit.Message.Length > 50 ?
                string.Concat(commit.Message.AsSpan(0, 50), "...") :
                commit.Message;
            WriteLine($"{commit.Id} {messagePreview.ReplaceLineEndings("")}");
        }
        await StopAsync(stoppingToken);
    }
}