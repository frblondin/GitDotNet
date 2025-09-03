using System.IO.Compression;
using System.Linq.Expressions;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using GitDotNet.Tests.Properties;
using GitDotNet.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace GitDotNet.Tests.Benchmarks;

public class ArchiveBenchmark
{
    [Test]
#if !RELEASE
    [Ignore("Ignored if not running in RELEAE mode.")]
#endif
    public void RunBenchmark()
    {
        var summary = BenchmarkRunner.Run<Cases>(
            ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithLaunchCount(1)
                .WithWarmupCount(0)
                .WithIterationCount(2)));
        var native = summary.GetActionMeanDuration<Cases>(c => c.NativeGitArchive());
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        var gitDotNet = summary.GetActionMeanDuration<Cases>(c => c.GitDotNet());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

        Assert.That(gitDotNet, Is.LessThan(native),
            "GitDotNet archive should be faster than native git archive.");
    }

    public class Cases
    {
        [Benchmark(Baseline = true)]
        public void NativeGitArchive()
        {
            var zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            try
            {
                GitCliCommand.Execute(_path, $@"archive -o ""{zipPath}"" HEAD");
                Console.WriteLine($"Zip file created at: {zipPath} of size {new FileInfo(zipPath).Length}");
            }
            finally
            {
                File.Delete(zipPath);
            }
        }

        [Benchmark]
        public async Task GitDotNet()
        {
            using var connection = new ServiceCollection()
                .AddMemoryCache()
                .AddGitDotNet(o => o.SlidingCacheExpiration = TimeSpan.FromSeconds(1))
                .BuildServiceProvider()
                .GetRequiredService<GitConnectionProvider>()
                .Invoke(_path)!;
            await Archive(connection);
        }

        private static async Task Archive(IGitConnection connection)
        {
            var zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            try
            {
                using (var zipStream = new FileStream(zipPath, System.IO.FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var channel = Channel.CreateUnbounded<Task<(GitPath Path, Stream Stream)>>();
                    await ReadRepository(connection, channel).ConfigureAwait(false);
                    await WriteArchive(archive, channel).ConfigureAwait(false);
                }

                Console.WriteLine($"Zip file created at: {zipPath} of size {new FileInfo(zipPath).Length}");
            }
            finally
            {
                File.Delete(zipPath);
            }
        }

        private static async Task ReadRepository(IGitConnection connection, Channel<Task<(GitPath Path, Stream Stream)>> channel)
        {
            var tip = await connection.Head.GetTipAsync().ConfigureAwait(false);
            var root = await tip.GetRootTreeAsync().ConfigureAwait(false);
            await root.GetAllBlobEntriesAsync(channel, async data =>
            {
                var gitEntry = await data.BlobEntry.GetEntryAsync<BlobEntry>().ConfigureAwait(false);
                return (data.Path, gitEntry.OpenRead());
            }).ConfigureAwait(false);
        }

        private static async Task WriteArchive(ZipArchive archive, Channel<Task<(GitPath Path, Stream Stream)>> channel)
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var dataTask))
                {
                    var data = await dataTask.ConfigureAwait(false);
                    var entry = archive.CreateEntry(data.Path.ToString(), CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    await data.Stream.CopyToAsync(entryStream).ConfigureAwait(false);
                    await data.Stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static readonly string _path = Path.Combine(Path.GetTempPath(), nameof(Cases));

        [GlobalSetup]
        public void Setup()
        {
            if (!Directory.Exists(_path))
            {
                ZipFile.ExtractToDirectory(new MemoryStream(Resource.BenchmarkRepository), _path, overwriteFiles: true);
            }
        }
    }
}