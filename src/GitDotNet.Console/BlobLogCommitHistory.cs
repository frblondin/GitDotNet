using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public class BlobLogCommitHistory(GitConnectionProvider factory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        using var connection = factory.Invoke(path);
        var tip = connection.Head.Tip ?? throw new NotSupportedException("Branch has no tip commit.");
        TreeEntryItem? entry = null;
        var root = await tip.GetRootTreeAsync();
        var blob = await entry!.GetEntryAsync<BlobEntry>();
        await foreach (var commit in blob.GetLogAsync(
            connection.Head,
            LogOptions.Default with { SortBy = LogTraversal.FirstParentOnly }))
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