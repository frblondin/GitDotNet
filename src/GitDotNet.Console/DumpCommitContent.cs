using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using static System.Console;
using static GitDotNet.Console.Tools.InfoInput;

namespace GitDotNet.Console;

public class DumpCommitContent(GitConnectionProvider factory) : BackgroundService
{
    public const bool ShowBlobContent = false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        var showRecords = InputData("Show records - slow? (y/n)",
            x => x.Equals("y", StringComparison.OrdinalIgnoreCase) || x.Equals("n", StringComparison.OrdinalIgnoreCase),
            "n").Equals("y", StringComparison.OrdinalIgnoreCase);
        using var connection = factory.Invoke(path);
        var tip = await connection.Head.GetTipAsync();
        var root = await tip.GetRootTreeAsync();
        var stopwatch = Stopwatch.StartNew();
        var channel = Channel.CreateUnbounded<Task<(GitPath Path, long Length)>>();
        await root.GetAllBlobEntriesAsync(channel, async data =>
        {
            var gitEntry = await data.BlobEntry.GetEntryAsync<BlobEntry>();
            using var stream = gitEntry.OpenRead();
            if (showRecords) WriteLine($"{data.Path}: {stream.Length} bytes");
            return (data.Path, stream.Length);
        }, cancellationToken: stoppingToken);
        WriteLine("Completed reading repository in {0} ms", stopwatch.ElapsedMilliseconds);
        await StopAsync(stoppingToken);
    }
}
