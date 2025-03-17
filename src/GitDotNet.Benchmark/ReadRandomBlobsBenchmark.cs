using BenchmarkDotNet.Attributes;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace GitDotNet;

[MemoryDiagnoser]
//[NativeMemoryProfiler]
public class ReadRandomBlobsBenchmark
{
    public const string Path = @"<Provide valid path to repository>";

    private IList<HashId>? _hashes;
    private GitConnection? _GitDotNetNoCacheExpiration, _GitDotNet10MsCache, _GitDotNet100MsCache;
    private Repository? _libgit2sharp;
    private readonly Random? _random = new();

    [GlobalSetup]
    public void GetServiceCollection()
    {
        _GitDotNetNoCacheExpiration = CreateConnectionProvider(o => o.SlidingCacheExpiration = null).Invoke(Path);
        _GitDotNet10MsCache = CreateConnectionProvider(o => o.SlidingCacheExpiration = TimeSpan.FromMilliseconds(10)).Invoke(Path);
        _GitDotNet100MsCache = CreateConnectionProvider(o => o.SlidingCacheExpiration = TimeSpan.FromMilliseconds(100)).Invoke(Path);
        _libgit2sharp = new Repository(Path);
        _hashes = GetBlobHashesAsync(CreateConnectionProvider()).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    internal static GitConnectionProvider CreateConnectionProvider(Action<GitConnection.Options>? options = null) =>
        new ServiceCollection()
        .AddMemoryCache()
        .AddGitDotNet(options)
        .BuildServiceProvider()
        .GetRequiredService<GitConnectionProvider>();

    public static async Task<IList<HashId>> GetBlobHashesAsync(GitConnectionProvider provider)
    {
        var result = new ConcurrentBag<HashId>();
        using var connection = provider(Path);
        foreach (var pack in ((IObjectResolverInternal)connection.Objects).PackReaders.Values)
        {
            if (result.Count >= 1_000) break;
            await foreach (var (_, hash) in pack.Value.GetHashesAsync())
            {
                if (result.Count >= 1_000) break;
                var entry = await connection.Objects.GetAsync<Entry>(hash);
                if (entry is BlobEntry blob && !blob.IsLfs)
                {
                    using var stream = blob.OpenRead();
                    if (stream.Length > 1_000 && stream.Length < 3_000) result.Add(hash);
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

    [Benchmark]
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