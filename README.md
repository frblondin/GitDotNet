GitDotNet is a .NET library designed to read Git repositories in a fully native .NET environment. It is optimized for minimal memory footprint and efficient data retrieval from repositories.

It also supports writing, but it doesn't write itself: it uses git [fast-import](https://git-scm.com/docs/git-fast-import) to write objects and refs to the repository. This guarantees best writing performance, safe git data writing, and avoid having to reinvent the wheel (object compression...).

## Features

- Supports .NET 8 and 9. Note that .NET 9 is highly recommended because it uses [zlib-ng](https://github.com/zlib-ng/zlib-ng) which is much faster.
- Read and parse Git objects (commits, trees, blobs, tags, index...) natively in .NET.
- Write using the git [fast-import](https://git-scm.com/docs/git-fast-import) feature for high efficiency and reliability.
- Use of memory-mapped files for high-performance .git repository data reading.
- Asynchronous methods for efficient data retrieval.
- Minimal memory footprint with lazy loading of data and usage of data streaming.
- Comparison of git commits and trees, along with renaming detection and git patch creation.

<details>
<summary>Detailed status...</summary>
As per high-level git features, the following is the current status of the project:

* [x] ~~clone~~: `GitConnection.Create(path, isBare)`
* [x] ~~fetch~~: `connection.Branches["main"].Fetch()`
* [ ] blame
* [ ] push
* [ ] reset
* [ ] status
* [x] ~~commit/trees diff~~ (including renaming detection): `connection.CompareAsync("HEAD~10", "HEAD")`
* [ ] merge
    - [ ] blobs
    - [ ] trees
    - [ ] commits
* [ ] rebase
* [x] ~~commit~~ `await connection.CommitAsync("main", c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")), connection.CreateCommit(...))`
* [ ] worktree checkout
* [ ] worktree stream
* [x] ~~read history~~: `connection.GetLogAsync("HEAD~1", LogOptions.Default with { ... })`, `await (foreach commint in connection.Branches["fix/bug"])`
* [x] ~~.NET native reading of objects~~: `connection.GetAsync<BlobEntry>("1aad9b571c0b84031191ab76e06fae4ba1f981bc")`
* [x] ~~.NET native reading of `.git/index`~~: `connection.Index.GetEntriesAsync()`
* [x] ~~writing of objects~~ (uses [fast-import](https://git-scm.com/docs/git-fast-import)): `connection.CommitAsync("main", c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")), connection.CreateCommit(...))`
* [ ] writing of `.git/index`
* [x] ~~reading of git configuration~~: `connection.Config.GetProperty("user", "email")`
* [ ] writing of git configuration

_Note that the main purpose of DotNetGit is to provide high speed reading. Writing can be done through commands._
</details>

## Installation

To install GitDotNet, add the `GitDotNet` or `GitDotNet.Microsoft.Extensions.DependencyInjection` NuGet package to your project.

## Usage

1. Add GitDotNet dependencies to your `IServiceCollection` using the `AddGitDotNet` extension method. This will inject `ConnectionProvider` and other required services.
2. Use the `Objects.GetAsync` method to retrieve Git objects by their hash.
3. Use the provided methods to parse and display the content of commits, trees, blobs, and tags.

```csharp
using Microsoft.Extensions.DependencyInjection;
using GitDotNet;

var services = new ServiceCollection()
    .AddMemoryCache(o => o.SizeLimit = 10_000_000) // Each entry size is always 1
    .AddGitDotNet();
var provider = services.BuildServiceProvider().GetRequiredService<GitConnectionProvider>();

using var connection = await provider("path/to/repo");
var commit = await connection.Branches["main"].Tip;
Console.WriteLine($"Commit Message: {commit.Message}");

var tree = await commit.GetTreeAsync();
```

## Benchmarks

### GitDotNet vs. LibGit2Sharp: reading random blobs of size from 1kB to 3kB

| Method       | Runtime  | Mean         | Error       | StdDev       | Ratio | RatioSD | Gen0   | Gen1   | Gen2   | Allocated | Alloc Ratio |
|------------- |--------- |-------------:|------------:|-------------:|------:|--------:|-------:|-------:|-------:|----------:|------------:|
| GitDotNet    | .NET 9.0 |     266.5 ns |     5.21 ns |      6.95 ns |  1.00 |    0.04 | 0.0243 |      - |      - |     464 B |        1.00 |
| GitDotNet    | .NET 8.0 |     322.1 ns |     4.07 ns |      3.61 ns |  1.21 |    0.03 | 0.0243 |      - |      - |     464 B |        1.00 |
| LibGit2Sharp | .NET 9.0 |  23,787.7 ns |   428.43 ns |    379.79 ns |  1.00 |    0.02 | 0.1221 | 0.0305 |      - |    2384 B |        1.00 |

### GitDotNet vs. native git 2.47.1.windows.1: archiving git repository

| Method                         | Mean    | Error   | StdDev   |
|------------------------------- |--------:|--------:|---------:|
| GitDotNet                      | 6.200 s | 2.676 s | 0.1467 s |
| git archive -o result.zip HEAD | 7.357 s | 6.893 s | 0.3778 s |

## Contributing

Contributions are welcome! Please open an issue or submit a pull request on GitHub.

## License

This project is licensed under the MIT License.