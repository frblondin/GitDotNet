using System.Collections.Concurrent;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using GitDotNet.Tests.Properties;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace GitDotNet.Tests.Benchmarks;

public class ReadRandomBlobsBenchmark
{
    [Test]
#if !RELEASE
    [Ignore("Ignored if not running in RELEAE mode.")]
#endif
    public void RunBenchmark()
    {
        var summary = BenchmarkRunner.Run<Cases>(
            ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default));
        var libGit2Sharp = summary.GetActionMeanDuration<Cases>(c => c.LibGit2Sharp());
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        var gitDotNet = summary.GetActionMeanDuration<Cases>(c => c.GitDotNet100MsCache());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

        Assert.That(gitDotNet, Is.LessThan(libGit2Sharp / 10),
            "GitDotNet reading should be at least 10 times faster than LibGit2Sharp.");
    }

    [MemoryDiagnoser]
    public class Cases
    {
        private IList<HashId>? _hashes;
        private IGitConnection? _GitDotNetNoCacheExpiration, _GitDotNet10MsCache, _GitDotNet100MsCache;
        private Repository? _libgit2sharp;
        private readonly Random? _random = new();
        private static readonly string _path = Path.Combine(Path.GetTempPath(), nameof(Cases));

        [GlobalSetup]
        public void GetServiceCollection()
        {
            if (!Directory.Exists(_path))
            {
                ZipFile.ExtractToDirectory(new MemoryStream(Resource.BenchmarkRepository), _path, overwriteFiles: true);
            }
            _GitDotNetNoCacheExpiration = CreateConnectionProvider(o => o.SlidingCacheExpiration = null).Invoke(_path);
            _GitDotNet10MsCache = CreateConnectionProvider(o => o.SlidingCacheExpiration = TimeSpan.FromMilliseconds(10)).Invoke(_path);
            _GitDotNet100MsCache = CreateConnectionProvider(o => o.SlidingCacheExpiration = TimeSpan.FromMilliseconds(100)).Invoke(_path);
            _libgit2sharp = new Repository(_path);
            _hashes = GetBlobHashesAsync(CreateConnectionProvider()).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        internal static GitConnectionProvider CreateConnectionProvider(Action<IGitConnection.Options>? options = null) =>
            new ServiceCollection()
            .AddMemoryCache()
            .AddGitDotNet(options)
            .BuildServiceProvider()
            .GetRequiredService<GitConnectionProvider>();

        public static async Task<IList<HashId>> GetBlobHashesAsync(GitConnectionProvider provider)
        {
            var result = new ConcurrentBag<HashId>();
            using var connection = provider(_path);
            foreach (var pack in ((IObjectResolverInternal)connection.Objects).PackManager.Indices)
            {
                if (result.Count >= 1_000)
                    break;
                await foreach (var (_, hash) in pack.GetHashesAsync())
                {
                    if (result.Count >= 1_000)
                        break;
                    var entry = await connection.Objects.GetAsync<Entry>(hash);
                    if (entry is BlobEntry blob && !blob.IsLfs)
                    {
                        using var stream = blob.OpenRead();
                        result.Add(hash);
                    }
                }
            }
            Console.WriteLine($"{result.Count} hashes found in repository.");
            return [.. result];
        }

        [Benchmark]
        public async Task GitDotNetNoCacheExpiration()
        {
            var entry = await _GitDotNetNoCacheExpiration!.Objects.GetAsync<BlobEntry>(GetRandomId());
            using var stream = entry.OpenRead();
            await stream.CopyToAsync(Stream.Null);
        }

        [Benchmark]
        public async Task GitDotNet10MsCache()
        {
            var entry = await _GitDotNet10MsCache!.Objects.GetAsync<BlobEntry>(GetRandomId());
            using var stream = entry.OpenRead();
            await stream.CopyToAsync(Stream.Null);
        }

        [Benchmark]
        public async Task GitDotNet100MsCache()
        {
            var entry = await _GitDotNet100MsCache!.Objects.GetAsync<BlobEntry>(GetRandomId());
            using var stream = entry.OpenRead();
            await stream.CopyToAsync(Stream.Null);
        }

        [Benchmark(Baseline = true)]
        public void LibGit2Sharp()
        {
            var id = GetRandomId();
            var entry = _libgit2sharp!.Lookup(new ObjectId([.. id.Hash]));
            var blob = entry.Peel<Blob>();
            blob.GetContentStream().CopyTo(Stream.Null);
        }

        private HashId GetRandomId()
        {
            var index = _random!.Next(_hashes!.Count);
            return _hashes[index];
        }
    }
}