using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Text;
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
    public async Task ShouldReturnTipCommitHash()
    {
        // Arrange
        using var sut = CreateProviderUsingFakeFileSystem(out _).Invoke(".git");

        // Act
        var tip = await sut.Head.GetTipAsync();

        // Assert
        tip.Id.ToString().Should().Be("1aad9b571c0b84031191ab76e06fae4ba1f981bc");
    }

    [Test]
    public async Task DiffLastTwoCommits()
    {
        // Arrange
        using var sut = CreateProviderUsingFakeFileSystem(out _).Invoke(".git");

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
        using (CreateProviderUsingFakeFileSystem(out var fileSystem).Invoke(".git"))
        {
            // Assert
            fileSystem.FileExists(".git/index.lock").Should().BeTrue();
        }
        await Task.CompletedTask;
    }

    [Test]
    public void ReleasesLockWhenRepositoryDisposed()
    {
        // Arrange & Act
        MockFileSystem fileSystem;
        using (CreateProviderUsingFakeFileSystem(out fileSystem).Invoke(".git"))
        {
        }

        // Assert
        fileSystem.FileExists(".git/index.lock").Should().BeFalse();
    }

    [Test]
    public async Task AddCommit()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var tip = await sut.Head.GetTipAsync();
        var commit = await sut.CommitAsync("main",
                                           c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")),
                                           sut.CreateCommit("Commit message",
                                                            new("test", "test@corporate.com", DateTimeOffset.Now),
                                                            new("test", "test@corporate.com", DateTimeOffset.Now)),
                                           updateBranch: true);

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
    public async Task AddCommitWithoutUpdatingBranch()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var tip = await sut.Head.GetTipAsync();
        var commit = await sut.CommitAsync("main",
                                           c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")),
                                           sut.CreateCommit("Commit message",
                                                            new("test", "test@corporate.com", DateTimeOffset.Now),
                                                            new("test", "test@corporate.com", DateTimeOffset.Now)),
                                           updateBranch: false);

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
    public async Task CreateBareRepositoryAndAddCommit()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

        // Act
        var commit = await sut.CommitAsync("main",
                                           c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")),
                                           sut.CreateCommit("Commit message",
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
        var commits = await sut.GetLogAsync("HEAD", LogOptions.Default with { SortBy = LogTraversal.FirstParentOnly })
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
        var blob = await root.GetPathAsync("Applications/ss04fto6lzk5/ss04fto6lzk5.json");

        // Assert
        blob!.Name.Should().Be("ss04fto6lzk5.json");
    }

    private static GitConnectionProvider CreateProvider() => CreateProvider(out var _);

    public static GitConnectionProvider CreateProvider(out ServiceProvider provider)
    {
        var collection = new ServiceCollection()
            .AddMemoryCache()
            .AddGitDotNet();
        provider = collection.BuildServiceProvider();
        return provider.GetRequiredService<GitConnectionProvider>();
    }

    public static GitConnectionProvider CreateProviderUsingFakeFileSystem(out MockFileSystem fileSystem)
    {
        fileSystem = new MockFileSystem().AddZipContent(Resource.CompleteRepository);
        var captured = fileSystem;
        var collection = new ServiceCollection()
            .AddMemoryCache()
            .AddGitDotNet()
            .AddSingleton<IFileSystem>(fileSystem)
            .AddScoped<FileOffsetStreamReaderFactory>(sp => path => captured.CreateOffsetReader(path))
            .AddScoped<RepositoryInfoFactory>(sp => path => CreateBareInfoProvider(path, sp.GetRequiredService<ConfigReaderFactory>(), captured));
        return collection.BuildServiceProvider().GetRequiredService<GitConnectionProvider>();
    }
}
