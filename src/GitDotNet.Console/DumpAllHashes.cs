using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public class DumpAllHashes(GitConnectionProvider factory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        using var connection = factory(path);
        var ids = connection.Objects.PackReaders.Values.SelectMany(
            r => r.Value.GetHashesAsync().ToBlockingEnumerable().Select(
                data => data.Id)).ToList();
        foreach (var id in ids)
        {
            try
            {
                var entry = await connection.Objects.GetAsync<Entry>(id);
                //Console.WriteLine(entry);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while reading {id}.", ex);
            }
        }
        await StopAsync(stoppingToken);
    }

    private static void Print(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            WriteLine(entry);
        }
    }
}
