using System.IO.Compression;
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
        using var connection = factory.Invoke(path);
        var tip = await connection.Head.GetTipAsync();
        var root = await tip.GetRootTreeAsync();
        var channel = Channel.CreateUnbounded<Task<(GitPath Path, long Length)>>();
        await root.GetAllBlobEntriesAsync(channel, async data =>
        {
            var gitEntry = await data.BlobEntry.GetEntryAsync<BlobEntry>();
            using var stream = gitEntry.OpenRead();
            return (data.Path, stream.Length);
        });
        while (await channel.Reader.WaitToReadAsync(stoppingToken))
        {
            while (channel.Reader.TryRead(out var dataTask))
            {
                var data = await dataTask;
                WriteLine($"{data.Path}: {data.Length} bytes");
            }
        }
        await StopAsync(stoppingToken);
    }
}
