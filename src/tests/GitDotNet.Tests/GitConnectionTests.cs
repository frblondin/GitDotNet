using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using GitDotNet.Tools;
using Microsoft.Extensions.DependencyInjection;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests;

public class GitConnectionTests
{
    [Test]
    public void IsValidReturnsTrueForValidPath()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act, Assert
        using (new AssertionScope())
        {
            GitConnection.IsValid(folder).Should().BeTrue();
            GitConnection.IsValid($"{folder}/.git").Should().BeTrue();
        }
    }

    [Test]
    public void IsValidReturnsFalseForNestedPath()
    {
        // Act, Assert
        GitConnection.IsValid(".").Should().BeFalse();
    }

    [Test]
    public async Task ShouldReturnTipCommitHash()
    {
        // Arrange
        var fileSystem = default(MockFileSystem);
        using var sut = CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git");

        // Act
        var tip = await sut.Head.GetTipAsync();

        // Assert
        tip.Id.ToString().Should().Be("1aad9b571c0b84031191ab76e06fae4ba1f981bc");
    }

    [Test]
    public async Task Clone()
    {
        // Arrange
        var source = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepositoryWithRename), source, overwriteFiles: true);
        var destination = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{TestContext.CurrentContext.Test.Name}_dest");
        TestUtils.ForceDeleteDirectory(destination);

        // Act
        GitConnection.Clone(destination, source, new(IsBare: true));
        using var sut = CreateProvider().Invoke(destination);

        // Assert
        using (new AssertionScope())
        {
            sut.Info.Config.IsBare.Should().BeTrue();
            sut.Head.Tip!.Id.ToString().Should().Be("b2cb7f24a9a18e72d359ae47fb15dde6b8559d51");
        }
    }

    [Test]
    public async Task Branch()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke(folder);

        // Act
        var branch = sut.Branches.Add("new_branch", "main~1");

        // Assert
        using (new AssertionScope())
        {
            branch.CanonicalName.Should().Be("refs/heads/new_branch");
            var tip = await branch.GetTipAsync();
            tip.Id.ToString().Should().Be("fee84b5575de791d1ac1edb089a63ab85d504f3c");
        }
    }

    [Test]
    public async Task DiffLastTwoCommits()
    {
        // Arrange
        var fileSystem = default(MockFileSystem);
        using var sut = CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git");

        // Act
        var diff = await sut.CompareAsync("HEAD~1", "HEAD");

        // Assert
        using (new AssertionScope())
        {
            diff.Should().HaveCount(1);
            diff.Should().Contain(c =>
                c.Type == ChangeType.Added &&
                c.NewPath!.ToString() == "Applications/ss04fto6lzk5/Pages/u5fq4plsthbh/u5fq4plsthbh.json");
        }
    }

    [Test]
    public async Task LocksRepository()
    {
        // Arrange & Act
        var fileSystem = default(MockFileSystem);
        using (CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"))
        {
            // Assert
            fileSystem!.FileExists(".git/index.lock").Should().BeTrue();
        }
        await Task.CompletedTask;
    }

    [Test]
    public void ReleasesLockWhenRepositoryDisposed()
    {
        // Arrange & Act
        var fileSystem = default(MockFileSystem);
        using (CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"))
        {
        }

        // Assert
        fileSystem!.FileExists(".git/index.lock").Should().BeFalse();
    }

    [Test]
    public async Task AddBlobCommit()
    {
        // Arrange & Act
        var (sut, tip, commit) = await CreateCommitWithTransformation(
            c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")));

        // Assert
        var patch = new MemoryStream();
        var diff = await sut.CompareAsync("HEAD~1", "HEAD", patch);
        using (new AssertionScope())
        {
            tip = await sut.Head.GetTipAsync();
            tip.Id.Should().Be(commit.Id);
            diff.Should().HaveCount(1);
            diff[0].Type.Should().Be(ChangeType.Added);
            diff[0].NewPath!.ToString().Should().Be("test.txt");
            var newBlob = await diff[0].New!.GetEntryAsync<BlobEntry>();
            newBlob.GetText().Should().Be("foo");
            patch.Position = 0;
            new StreamReader(patch).ReadToEnd().Should().Contain(
                $" 1 insertion(+)\n\n--- a/dev/null\n+++ b/test.txt\nindex 1aad9b5..{commit.Id.ToString()[..7]} 100644\n@@ -1,1 +1,1 @@\n+foo\n");
        }
    }


    [Test]
    public async Task RemoveBlobCommit()
    {
        // Arrange & Act
        var (sut, tip, commit) = await CreateCommitWithTransformation(
            c => c.Remove("Applications/ss04fto6lzk5/ss04fto6lzk5.json"));

        // Assert
        var patch = new MemoryStream();
        var diff = await sut.CompareAsync("HEAD~1", "HEAD", patch);
        using (new AssertionScope())
        {
            tip = await sut.Head.GetTipAsync();
            tip.Id.Should().Be(commit.Id);
            diff.Should().HaveCount(1);
            diff[0].Type.Should().Be(ChangeType.Removed);
            diff[0].OldPath!.ToString().Should().Be("Applications/ss04fto6lzk5/ss04fto6lzk5.json");
            patch.Position = 0;
            new StreamReader(patch).ReadToEnd().Should().Contain(
                $" 1 deletion(-)\n\n--- a/Applications/ss04fto6lzk5/ss04fto6lzk5.json\n+++ b/dev/null");
        }
    }

    [Test]
    public async Task AddCommitWithoutUpdatingBranch()
    {
        // Arrange & Act
        var (sut, tip, commit) = await CreateCommitWithTransformation(
            c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")), updateBranch: false);

        // Assert
        var diff = await sut.CompareAsync("HEAD", commit.Id.ToString());
        using (new AssertionScope())
        {
            diff.Should().HaveCount(1);
            diff[0].Type.Should().Be(ChangeType.Added);
            diff[0].NewPath!.ToString().Should().Be("test.txt");
            var newBlob = await diff[0].New!.GetEntryAsync<BlobEntry>();
            newBlob.GetText().Should().Be("foo");
        }
    }


    [Test]
    public async Task AddFirstCommitWithoutUpdatingBranch()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        GitConnection.Create(folder, isBare: true);
        var sut = CreateProvider().Invoke(folder);

        // Act
        var commit = await sut.CommitAsync("main",
            c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")),
            sut.CreateCommit("Commit message",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)),
            new(UpdateBranch: false));

        // Assert
        var diff = await sut.CompareAsync(null, commit.Id.ToString());
        using (new AssertionScope())
        {
            diff.Should().HaveCount(1);
            diff[0].Type.Should().Be(ChangeType.Added);
            diff[0].NewPath!.ToString().Should().Be("test.txt");
            var newBlob = await diff[0].New!.GetEntryAsync<BlobEntry>();
            newBlob.GetText().Should().Be("foo");
        }
    }

    [Test]
    public async Task StageFiles()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder);
        using (var writer = File.CreateText(Path.Combine(folder, "file.txt")))
        {
            writer.Write("foo");
        }
        var sut = CreateProvider().Invoke(folder);

        // Act
        sut.Index.AddEntries("*");

        // Assert
        using (new AssertionScope())
        {
            var entries = await sut.Index.GetEntriesAsync();
            entries.Should().HaveCount(1);
            entries[0].Path.ToString().Should().Be("file.txt");
            entries[0].Type.Should().Be(IndexEntryType.Regular);
            entries[0].UnixPermissions.Should().Be(420);
            var blob = await entries[0].GetEntryAsync<BlobEntry>();
            blob.GetText().Should().Be("foo");
        }
    }

    [Test]
    public async Task AddCommitThroughFileSystem()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder);
        using (var writer = File.CreateText(Path.Combine(folder, "file.txt")))
        {
            writer.Write("foo");
        }
        var sut = CreateProvider().Invoke(folder);
        sut.Index.AddEntries("*");

        // Act
        var commit = await sut.CommitAsync("foo",
                                           new Signature("test", "test@corporate.com", DateTimeOffset.Now),
                                           new Signature("JC Van Damme", "test@corporate.com", DateTimeOffset.Now));

        // Assert
        using (new AssertionScope())
        {
            commit.Message.Should().Be("foo");
            commit.Author!.Name.Should().Be("test");
            commit.Author!.Email.Should().Be("test@corporate.com");
            commit.Committer!.Name.Should().Be("JC Van Damme");
            commit.Committer!.Email.Should().Be("test@corporate.com");
            var diff = await sut.CompareAsync(null, commit.Id.ToString());
            diff.Should().HaveCount(1);
            diff[0].Type.Should().Be(ChangeType.Added);
            diff[0].NewPath!.ToString().Should().Be("file.txt");
            var newBlob = await diff[0].New!.GetEntryAsync<BlobEntry>();
            newBlob.GetText().Should().Be("foo");
        }
    }

    private static async Task<(GitConnection sut, CommitEntry tip, CommitEntry commit)> CreateCommitWithTransformation(
        Func<ITransformationComposer, ITransformationComposer> transformations, bool updateBranch = true)
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var tip = await sut.Head.GetTipAsync();
        var commit = await sut.CommitAsync("main",
            transformations,
            sut.CreateCommit("Commit message",
                            [tip],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)),
            new(UpdateBranch: updateBranch));
        return (sut, tip, commit);
    }

    [Test]
    public async Task CreateBareRepositoryAndAddCommit()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

        // Act
        var commit = await sut.CommitAsync("main",
            c => c.AddOrUpdate("test.txt", "foo"),
            sut.CreateCommit("Commit message",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        // Assert
        using (new AssertionScope())
        {
            var headTip = await sut.Branches["refs/heads/main"].GetTipAsync();
            headTip.Id.Should().Be(commit.Id);
            var tree = await commit.GetRootTreeAsync();
            tree.Children.Should().HaveCount(1);
            tree.Children[0].Name.Should().Be("test.txt");
            var blob = await tree.Children[0].GetEntryAsync<BlobEntry>();
            blob.GetText().Should().Be("foo");
        }
    }

    [Test]
    public async Task GetLast2FirstParentLog()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var commits = await sut.GetLogAsync("HEAD", LogOptions.Default with
        {
            SortBy = LogTraversal.FirstParentOnly | LogTraversal.Topological,
            ExcludeReachableFrom = "fee84b5575de791d1ac1edb089a63ab85d504f3c",
        }).ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            commits.Should().HaveCount(1);
            commits[0].Message.Should().Be("message2efc0543-4f74-4401-83e3-dd5b66ec2016");
        }
    }

    [Test]
    public async Task GetAllMergeCommitsLog()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteMergeRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var commits = await sut.GetLogAsync("HEAD", LogOptions.Default with
        {
            SortBy = LogTraversal.Time,
            ExcludeReachableFrom = "de2877f9d577ee1efc6d770bdc37079ef293d946",
        }).ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            commits.Should().HaveCount(3);
            commits[0].Id.ToString().Should().Be("2e18708dc062f5d1c9f94e82b39081f9bdc17d82");
            commits[1].Id.ToString().Should().Be("57e779b92132f469060e6aaf2c5d61bb687e5c09");
            commits[2].Id.ToString().Should().Be("e159d393542c45cb945e892d6245fb9647e9df73");
        }
    }

    [Test]
    public async Task GetBlobEntryLog()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepositoryWithRename), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var root = await sut.Head.Tip.GetRootTreeAsync();
        var commits = await sut.GetLogAsync("HEAD", LogOptions.Default with { Path = "bar.txt", SortBy = LogTraversal.FirstParentOnly })
            .ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            commits.Should().HaveCount(3);
            commits[0].Id.ToString().Should().Be("b2cb7f24a9a18e72d359ae47fb15dde6b8559d51");
            commits[1].Id.ToString().Should().Be("499d54ed291b2f8ec13301b0985b11723855ab50");
            commits[2].Id.ToString().Should().Be("b71d400cba4ad7f9b7e6ad0e570b2cedbe0b181e");
        }
    }

    [Test]
    public async Task GetLast10Commits()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var commits = await sut.GetLogAsync("HEAD", LogOptions.Default)
            .Take(2)
            .ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            commits.Should().HaveCount(2);
            commits[0].Message.Should().Be("message2efc0543-4f74-4401-83e3-dd5b66ec2016");
            commits[1].Message.Should().Be("20c4b2e9-c474-481a-88d1-e60d297d1edb");
        }
    }

    [Test]
    public async Task GetMergeBase()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteMergeRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var commit = await sut.GetMergeBaseAsync("e159d393542c45cb945e892d6245fb9647e9df73", "57e779b92132f469060e6aaf2c5d61bb687e5c09");

        // Assert
        using (new AssertionScope())
        {
            commit.Id.ToString().Should().Be("de2877f9d577ee1efc6d770bdc37079ef293d946");
        }
    }

    [Test]
    public async Task GetBranchTips()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var branches = sut.Branches;

        // Assert
        using (new AssertionScope())
        {
            branches.Should().HaveCount(1);
            branches["main"].CanonicalName.Should().Be("refs/heads/main");
            branches["main"].FriendlyName.Should().Be("main");
            var tip = await (branches["main"]).GetTipAsync();
            tip.Id.ToString().Should().Be("1aad9b571c0b84031191ab76e06fae4ba1f981bc");
        }
    }

    [Test]
    public async Task NavigateTree()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var tip = sut.Head.Tip!;
        var root = await tip.GetRootTreeAsync();
        var blob = await root.GetFromPathAsync("Applications/ss04fto6lzk5/ss04fto6lzk5.json");

        // Assert
        blob!.Name.Should().Be("ss04fto6lzk5.json");
    }

    [Test]
    public async Task GetMultiParentMergeCommitDetails()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/HEAD", new MockFileData("ref: main"));
        fileSystem.AddFile(".git/refs/heads/main", new MockFileData("36010d7f7c4503ff54ba5989cbb0404ae989b5e7."));
        fileSystem.AddFile(".git/objects/info/commit-graph", new MockFileData(Resource.MultiParentMergeCommitGraph));
        var configReader = A.Fake<ConfigReader>(o => o.ConfigureFake(f =>
            A.CallTo(() => f.UseCommitGraph).Returns(true)));
        using var objectResolver = CreateObjectResolver(id => new CommitEntry(id, [], null!));
        using var graphReader = CommitGraphReader.Load(".git/objects",
                                                       objectResolver,
                                                       fileSystem,
                                                       p => fileSystem.CreateOffsetReader(p))!;
        using var connection = CreateProviderUsingFakeFileSystem(ref fileSystem, configReader, commitGraphReader: graphReader).Invoke(".git");

        // Act
        var commit = await connection.GetCommittishAsync("HEAD^3");

        // Assert
        commit.Id.ToString().Should().Be("a28d9681fdf40631632a42b303be274e3869d5d5");
    }

    internal static GitConnectionProvider CreateProvider() => CreateProvider(out var _);

    internal static GitConnectionProvider CreateProvider(out ServiceProvider provider)
    {
        var collection = new ServiceCollection()
            .AddMemoryCache()
            .AddGitDotNet();
        provider = collection.BuildServiceProvider();
        return provider.GetRequiredService<GitConnectionProvider>();
    }

    internal static GitConnectionProvider CreateProviderUsingFakeFileSystem(ref MockFileSystem? fileSystem,
                                                                            ConfigReader? configReader = null,
                                                                            IObjectResolver? objectResolver = null,
                                                                            CommitGraphReader? commitGraphReader = null)
    {
        fileSystem ??= new MockFileSystem().AddZipContent(Resource.CompleteRepository);
        var captured = fileSystem;
        var collection = new ServiceCollection()
            .AddMemoryCache()
            .AddGitDotNet()
            .AddSingleton<IFileSystem>(fileSystem)
            .AddScoped<FileOffsetStreamReaderFactory>(sp => path => captured.CreateOffsetReader(path))
            .AddScoped<RepositoryInfoFactory>(sp => path => CreateBareInfoProvider(path, sp.GetRequiredService<ConfigReaderFactory>(), captured));

        if (configReader != null)
            collection.AddSingleton<ConfigReaderFactory>((_) => configReader);

        if (objectResolver != null)
            collection.AddSingleton<ObjectsFactory>((_, _) => objectResolver);

        if (commitGraphReader != null)
            collection.AddSingleton<CommitGraphReaderFactory>((_, _) => commitGraphReader);

        return collection.BuildServiceProvider().GetRequiredService<GitConnectionProvider>();
    }
}
