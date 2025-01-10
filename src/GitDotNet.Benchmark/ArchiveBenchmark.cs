using System.IO.Compression;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using GitDotNet.Tools;

namespace GitDotNet;

public class ArchiveBenchmark
{
    [Benchmark]
    public void NativeGitArchive()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        try
        {
            GitCliCommand.Execute(ReadRandomBlobsBenchmark.Path, $@"archive -o ""{zipPath}"" HEAD");
            Console.WriteLine($"Zip file created at: {zipPath} of size {new FileInfo(zipPath).Length}");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Benchmark]
    public async Task Archive()
    {
        var connection = ReadRandomBlobsBenchmark
            .CreateConnectionProvider(o => o.SlidingCacheExpiration = TimeSpan.FromSeconds(1))
            .Invoke(ReadRandomBlobsBenchmark.Path)!;
        await Archive(connection);
    }

    private static async Task Archive(GitConnection connection)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        try
        {
            using (var zipStream = new FileStream(zipPath, System.IO.FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var channel = Channel.CreateUnbounded<Task<(GitPath Path, Stream Stream)>>();
                await ReadRepository(connection, channel);
                await WriteArchive(archive, channel);
            }

            Console.WriteLine($"Zip file created at: {zipPath} of size {new FileInfo(zipPath).Length}");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static async Task ReadRepository(GitConnection connection, Channel<Task<(GitPath Path, Stream Stream)>> channel)
    {
        var tip = await connection.Head.GetTipAsync();
        var root = await tip.GetRootTreeAsync();
        await root.GetAllBlobEntriesAsync(channel, async data =>
        {
            var gitEntry = await data.BlobEntry.GetEntryAsync<BlobEntry>();
            return (data.Path, gitEntry.OpenRead());
        });
    }

    private static async Task WriteArchive(ZipArchive archive, Channel<Task<(GitPath Path, Stream Stream)>> channel)
    {
        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out var dataTask))
            {
                var data = await dataTask;
                var entry = archive.CreateEntry(data.Path.ToString(), CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                await data.Stream.CopyToAsync(entryStream);
                data.Stream.Dispose();
            }
        }
    }
}