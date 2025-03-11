using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public class DumpIndexContent(GitConnectionProvider factory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        using var connection = factory.Invoke(path);
        var entries = await connection.Index.GetEntriesAsync();
        Print(entries);
        await StopAsync(stoppingToken);
    }

    private static void Print(IEnumerable<IndexEntry> entries)
    {
        foreach (var entry in entries)
        {
            WriteLine(entry);
        }
    }
}
