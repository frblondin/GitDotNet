using System.Collections.Immutable;
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
using static GitDotNet.Tests.Helpers.DependencyInjectionProvider;
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
    public void IsValidReturnsFalseForNonExistingFolder()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name, "Foo");

        // Act, Assert
        GitConnection.IsValid(folder).Should().BeFalse();
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
    public void ShouldReturnDetachedHead()
    {
        // Arrange
        var fileSystem = default(MockFileSystem);
        using var sut = CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git");
        fileSystem!.AddFile(".git/HEAD", new MockFileData("1aad9b571c0b84031191ab76e06fae4ba1f981bc"));

        // Act
        var head = sut.Head;

        // Assert
        Assert.Multiple(() =>
        {
            head.Should().BeOfType<DetachedHead>();
            head.Tip!.ToString().Should().Be("1aad9b571c0b84031191ab76e06fae4ba1f981bc");
        });
    }

    [Test]
    public void Clone()
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
            sut.Head.Tip!.ToString().Should().Be("b2cb7f24a9a18e72d359ae47fb15dde6b8559d51");
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
        var branch = sut.Branches.Add("new_branch", await sut.GetCommittishAsync("main~1"));

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
        var (sut, _, commit) = await CreateCommitWithTransformation(
            c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")));

        using (sut)
        {
            // Assert
            var start = await sut.GetCommittishAsync("HEAD~1");
            var end = await sut.GetCommittishAsync("HEAD");
            var diff = await sut.CompareAsync(start, end);
            var patch = new MemoryStream();
            await new GitPatchCreator().WriteAsync(patch, start, end, diff);
            using (new AssertionScope())
            {
                var tip = await sut.Head.GetTipAsync();
                tip.Id.Should().Be(commit.Id);
                diff.Should().HaveCount(1);
                diff[0].Type.Should().Be(ChangeType.Added);
                diff[0].NewPath!.ToString().Should().Be("test.txt");
                var newBlob = await diff[0].New!.GetEntryAsync<BlobEntry>();
                newBlob.GetText().Should().Be("foo");
                patch.Position = 0;
                (await new StreamReader(patch).ReadToEndAsync()).Should().Contain(
                    $" 1 insertion(+)\n\n--- a/dev/null\n+++ b/test.txt\nindex 1aad9b5..{commit.Id.ToString()[..7]} 100644\n@@ -1,1 +1,1 @@\n+foo\n");
            }
        }
    }

    [Test]
    public async Task RemoveBlobCommit()
    {
        // Arrange & Act
        var (sut, _, commit) = await CreateCommitWithTransformation(
            c => c.Remove("Applications/ss04fto6lzk5/ss04fto6lzk5.json"));

        using (sut)
        {
            // Assert
            var start = await sut.GetCommittishAsync("HEAD~1");
            var end = await sut.GetCommittishAsync("HEAD");
            var diff = await sut.CompareAsync(start, end);
            var patch = new MemoryStream();
            await new GitPatchCreator().WriteAsync(patch, start, end, diff);
            using (new AssertionScope())
            {
                var tip = await sut.Head.GetTipAsync();
                tip.Id.Should().Be(commit.Id);
                diff.Should().HaveCount(1);
                diff[0].Type.Should().Be(ChangeType.Removed);
                diff[0].OldPath!.ToString().Should().Be("Applications/ss04fto6lzk5/ss04fto6lzk5.json");
                patch.Position = 0;
                (await new StreamReader(patch).ReadToEndAsync()).Should().Contain(
                    $" 1 deletion(-)\n\n--- a/Applications/ss04fto6lzk5/ss04fto6lzk5.json\n+++ b/dev/null");
            }
        }
    }

    [Test]
    public async Task AddCommitWithoutUpdatingBranch()
    {
        // Arrange & Act
        var (sut, _, commit) = await CreateCommitWithTransformation(
            c => c.AddOrUpdate("test.txt", Encoding.UTF8.GetBytes("foo")), updateBranch: false);

        using (sut)
        {
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
    }

    [Test]
    public async Task AddFirstCommitWithoutUpdatingBranch()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

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
        await using (var writer = File.CreateText(Path.Combine(folder, "file1.txt")))
        {
            await writer.WriteAsync("foo");
        }
        await using (var writer = File.CreateText(Path.Combine(folder, "file2.txt")))
        {
            await writer.WriteAsync("bar");
        }
        using var sut = CreateProvider().Invoke(folder);

        // Act
        sut.Index.AddEntries("*");

        // Assert
        using (new AssertionScope())
        {
            var entries = await sut.Index.GetEntriesAsync();
            entries.Should().HaveCount(2);
            entries[1].Path.ToString().Should().Be("file2.txt");
            entries[1].Type.Should().Be(IndexEntryType.Regular);
            entries[1].UnixPermissions.Should().Be(420);
            var blob = await entries[1].GetEntryAsync<BlobEntry>();
            blob.GetText().Should().Be("bar");
        }
    }

    [Test]
    public async Task AddCommitThroughFileSystem()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder);
        await using (var writer = File.CreateText(Path.Combine(folder, "file.txt")))
        {
            await writer.WriteAsync("foo");
        }
        using var sut = CreateProvider().Invoke(folder);
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

    private static async Task<(IGitConnection sut, CommitEntry tip, CommitEntry commit)> CreateCommitWithTransformation(
        Action<ITransformationComposer> transformations, bool updateBranch = true)
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
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
        var baseTemplate = Encoding.UTF8.GetString(Resource.SourceFile);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

        // Act
        var commit = await sut.CommitAsync("main",
            c =>
            {
                for (int i = 0; i < 1_000; i++)
                {
                    c.AddOrUpdate($"a/b/GeneratedClass{i:000}.cs", $"{baseTemplate}{i:000}");
                }
            },
            sut.CreateCommit("Commit message",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        // Assert
        using (new AssertionScope())
        {
            var headTip = await sut.Branches["refs/heads/main"].GetTipAsync();
            headTip.Id.Should().Be(commit.Id);
            var rootTree = await commit.GetRootTreeAsync();
            var tree = await (await rootTree.GetFromPathAsync("a/b"))!.GetEntryAsync<TreeEntry>();
            tree.Children.Should().HaveCount(1_000);
            var blob = await (await tree.GetFromPathAsync("GeneratedClass500.cs"))!.GetEntryAsync<BlobEntry>();
            blob.GetText().Should().Be($"{baseTemplate}500");
        }
    }

    [Test]
    public async Task CreateBareRepositoryWithManyEmptyFiles()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

        // Act - Create 1000 empty files
        var commit = await sut.CommitAsync("main",
            c =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    c.AddOrUpdate($"empty_file_{i:D4}.txt", "");
                }
            },
            sut.CreateCommit("Initial commit with 1000 empty files",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        // Assert
        using (new AssertionScope())
        {
            var headTip = await sut.Branches["refs/heads/main"].GetTipAsync();
            headTip.Id.Should().Be(commit.Id);

            var tree = await commit.GetRootTreeAsync();
            tree.Children.Should().HaveCount(1000, "Should have 1000 empty files");
        }
    }

    [Test]
    public async Task CreateBareRepositoryAndAddCommitWithSmallRandomDiffs_ShowsDeltaCompression()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        var baseString = string.Concat(Enumerable.Range(0, 200).Select(_ => "The quick brown fox jumps over the lazy dog. "));
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

        // Create base content with realistic text patterns that should compress well
        var baseContent = $"{baseString}This is a common ending for all files.";

        // Track compression metrics for analysis
        var originalTotalSize = 0L;

        // Act - Create 100 files with small random character injections
        var commit = await sut.CommitAsync("main",
            c =>
            {
                var random = new Random(12345); // Fixed seed for reproducible tests

                for (int i = 0; i < 100; i++)
                {
                    // Inject 1-3 random characters at random positions for each file
                    var modifiedContent = InjectRandomCharacters(baseContent, random, injectionCount: random.Next(1, 4));
                    var fileName = $"document_{i:000}.txt";

                    c.AddOrUpdate(fileName, modifiedContent);
                    originalTotalSize += baseContent.Length;
                    // Note: We can't get compressed size here as it's calculated during pack writing
                }

                TestContext.Out.WriteLine($"Created 100 files with small random diffs");
                TestContext.Out.WriteLine($"Base content size: {baseContent.Length} characters");
                TestContext.Out.WriteLine($"Total original size: {originalTotalSize} bytes");
            },
            sut.CreateCommit("Initial commit with similar files having small random diffs",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        // Assert
        using (new AssertionScope())
        {
            var headTip = await sut.Branches["refs/heads/main"].GetTipAsync();
            headTip.Id.Should().Be(commit.Id);

            var tree = await commit.GetRootTreeAsync();
            tree.Children.Should().HaveCount(100, "Should have 100 files with small diffs");

            // Verify first file has expected structure
            var firstBlob = await tree.Children[0].GetEntryAsync<BlobEntry>();
            var firstContent = firstBlob.GetText();
            firstContent.Should().Contain("The quick brown fox jumps over the lazy dog",
                "Should contain the base pattern");
            firstContent.Should().Contain("This is a common ending for all files",
                "Should contain the common ending");

            // Verify files are actually different (small random diffs applied)
            var allContents = new List<string>();
            for (int i = 0; i < Math.Min(10, tree.Children.Count); i++) // Sample first 10 files
            {
                var blob = await tree.Children[i].GetEntryAsync<BlobEntry>();
                allContents.Add(blob.GetText()!);
            }

            // All files should be unique (very high probability with random injections)
            allContents.Should().OnlyHaveUniqueItems("Files should have unique content due to random character injections");

            // Log some analysis for manual verification
            await TestContext.Out.WriteLineAsync($"Sample file sizes: {string.Join(", ", allContents.Take(5).Select(c => c.Length))}");
            await TestContext.Out.WriteLineAsync("Delta compression effectiveness will be visible in pack file size vs sum of individual file sizes");

            // The real test is that this should create a highly compressed pack file
            // The delta algorithm should find that most content is shared between files
            // with only small literal differences for the injected characters
        }
    }

    private static string InjectRandomCharacters(string text, Random random, int injectionCount)
    {
        var chars = text.ToCharArray();
        var result = new List<char>(chars);

        // Character pool for injections (printable ASCII)
        const string injectableChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*()_+-=[]{}|;':\",./<>?";

        // Inject characters at random positions
        for (int i = 0; i < injectionCount; i++)
        {
            var insertPos = random.Next(0, result.Count);
            var charToInject = injectableChars[random.Next(injectableChars.Length)];
            result.Insert(insertPos, charToInject);
        }

        return new string(result.ToArray());
    }

    [Test]
    public async Task CreateBareRepositoryWithProgressiveDiffs_TestsDeltaChaining()
    {
        // Arrange - Test progressive diffs that should create delta chains
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = CreateProvider().Invoke(folder);

        var baseText = """
            # Software Development Best Practices

            ## Introduction
            This document outlines the best practices for software development
            in our organization. These practices are designed to improve code quality,
            maintainability, and team collaboration.

            ## Code Style Guidelines
            - Use meaningful variable names
            - Write self-documenting code
            - Follow consistent indentation
            - Add comments for complex logic

            ## Testing Requirements
            - Write unit tests for all new functions
            - Achieve minimum 80% code coverage
            - Include integration tests for APIs
            - Perform code reviews before merging

            ## Version Control
            - Use descriptive commit messages
            - Create feature branches for new work
            - Squash commits before merging
            - Tag releases with semantic versioning
            """;

        // Act - Create files with incremental changes (like editing a document over time)
        var commit = await sut.CommitAsync("main",
            c =>
            {
                var random = new Random(54321); // Fixed seed
                var currentText = baseText;

                for (int version = 0; version < 50; version++)
                {
                    // Each version adds 1-2 small edits to the previous version
                    // This should create an optimal scenario for delta chaining
                    currentText = ApplySmallEdits(currentText, random, editCount: random.Next(1, 3));

                    var fileName = $"best_practices_v{version:00}.md";
                    c.AddOrUpdate(fileName, currentText);
                }

                TestContext.Out.WriteLine("Created 50 progressive versions of a document");
                TestContext.Out.WriteLine($"Base document size: {baseText.Length} characters");
                TestContext.Out.WriteLine("Each version builds incrementally on the previous version");
            },
            sut.CreateCommit("Progressive document versions for delta chain testing",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        // Assert
        using (new AssertionScope())
        {
            var headTip = await sut.Branches["refs/heads/main"].GetTipAsync();
            headTip.Id.Should().Be(commit.Id);

            var tree = await commit.GetRootTreeAsync();
            tree.Children.Should().HaveCount(50, "Should have 50 progressive versions");

            // Verify the progression - each file should be similar to adjacent versions
            var firstBlob = await tree.Children[0].GetEntryAsync<BlobEntry>();
            var lastBlob = await tree.Children[49].GetEntryAsync<BlobEntry>();

            var firstText = firstBlob.GetText();
            var lastText = lastBlob.GetText();

            // Both should contain the core structure
            firstText.Should().Contain("Software Development Best Practices");
            lastText.Should().Contain("Software Development Best Practices");

            // But they should be different due to progressive edits
            firstText.Should().NotBe(lastText, "First and last versions should be different");

            await TestContext.Out.WriteLineAsync($"First version size: {firstText.Length} characters");
            await TestContext.Out.WriteLineAsync($"Last version size: {lastText.Length} characters");
            await TestContext.Out.WriteLineAsync("This scenario should demonstrate excellent delta chain compression");
        }
    }

    private static string ApplySmallEdits(string text, Random random, int editCount)
    {
        var result = text;

        for (int i = 0; i < editCount; i++)
        {
            // Make extremely conservative edits that won't break the main title
            var editType = random.Next(2); // 0=append to end only, 1=add blank line

            switch (editType)
            {
                case 0: // Append a small addition at the very end only
                    var additions = new[] { ".", " (updated)", " notes", " items" };
                    var addition = additions[random.Next(additions.Length)];
                    result += addition;
                    break;

                case 1: // Add extra whitespace/newlines (non-destructive)
                    if (result.Contains("## "))
                    {
                        // Add an extra newline before a section header
                        var index = result.IndexOf("## ", StringComparison.Ordinal);
                        if (index >= 0)
                        {
                            result = result.Substring(0, index) + "\n## " + result.Substring(index + 3);
                        }
                    }
                    break;
            }
        }

        return result;
    }

    [Test]
    public async Task GetLast2FirstParentLog()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var log = await sut.GetLogAsync("HEAD", LogOptions.Default with
        {
            SortBy = LogTraversal.FirstParentOnly | LogTraversal.Topological,
            ExcludeReachableFrom = "fee84b5575de791d1ac1edb089a63ab85d504f3c",
        }).ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            log.Should().HaveCount(1);
            var commit = await log[0].GetCommitAsync();
            commit.Message.Should().Be("message2efc0543-4f74-4401-83e3-dd5b66ec2016");
        }
    }

    [Test]
    public async Task GetAllMergeCommitsLog()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteMergeRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var log = await sut.GetLogAsync("HEAD", LogOptions.Default with
        {
            SortBy = LogTraversal.Time,
            ExcludeReachableFrom = "de2877f9d577ee1efc6d770bdc37079ef293d946",
        }).ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            log.Should().HaveCount(3);
            log[0].Id.ToString().Should().Be("2e18708dc062f5d1c9f94e82b39081f9bdc17d82");
            log[1].Id.ToString().Should().Be("57e779b92132f469060e6aaf2c5d61bb687e5c09");
            log[2].Id.ToString().Should().Be("e159d393542c45cb945e892d6245fb9647e9df73");
        }
    }

    [Test]
    public async Task GetBlobEntryLog()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepositoryWithRename), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var log = await sut.GetLogAsync("HEAD", LogOptions.Default with { Path = "bar.txt", SortBy = LogTraversal.FirstParentOnly })
            .ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            log.Should().HaveCount(3);
            log[0].Id.ToString().Should().Be("b2cb7f24a9a18e72d359ae47fb15dde6b8559d51");
            log[1].Id.ToString().Should().Be("499d54ed291b2f8ec13301b0985b11723855ab50");
            log[2].Id.ToString().Should().Be("b71d400cba4ad7f9b7e6ad0e570b2cedbe0b181e");
        }
    }

    [Test]
    public async Task GetLast10Commits()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var log = await sut.GetLogAsync("HEAD", LogOptions.Default)
            .Take(2)
            .ToListAsync();

        // Assert
        using (new AssertionScope())
        {
            log.Should().HaveCount(2);
            (await log[0].GetCommitAsync()).Message.Should().Be("message2efc0543-4f74-4401-83e3-dd5b66ec2016");
            (await log[1].GetCommitAsync()).Message.Should().Be("20c4b2e9-c474-481a-88d1-e60d297d1edb");
        }
    }

    [Test]
    public async Task GetMergeBase()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteMergeRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var commit = await sut.GetMergeBaseAsync("e159d393542c45cb945e892d6245fb9647e9df73", "57e779b92132f469060e6aaf2c5d61bb687e5c09");

        // Assert
        using (new AssertionScope())
        {
            commit!.Id.ToString().Should().Be("de2877f9d577ee1efc6d770bdc37079ef293d946");
        }
    }

    [Test]
    public async Task GetBranchTips()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
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
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke($"{folder}/.git");

        // Act
        var commit = await sut.GetCommittishAsync("HEAD");
        var root = await commit.GetRootTreeAsync();
        var blob = await root.GetFromPathAsync("Applications/ss04fto6lzk5/ss04fto6lzk5.json");

        // Assert
        blob!.Name.Should().Be("ss04fto6lzk5.json");
    }

    [Test]
    public async Task GetMultiParentMergeCommitDetails()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var tip = new HashId("36010d7f7c4503ff54ba5989cbb0404ae989b5e7");
        fileSystem.AddFile(".git/HEAD", new MockFileData("ref: main"));
        fileSystem.AddFile(".git/refs/heads/main", new MockFileData($"{tip}."));
        fileSystem.AddFile(".git/objects/info/commit-graph", new MockFileData(Resource.MultiParentMergeCommitGraph));
        var configReader = A.Fake<ConfigReader>(o => o.ConfigureFake(f =>
        {
            A.CallTo(() => f.UseCommitGraph).Returns(true);
            A.CallTo(() => f.GetSection(A<string>._, A<bool>._)).Returns(ImmutableDictionary<string, string>.Empty);
            A.CallTo(() => f.GetProperty(A<string>._, A<string>._, A<bool>._)).Returns(null);
        }));
        var graphReader = default(CommitGraphReader);
        using var objectResolver = CreateObjectResolver(id => graphReader!.Get(id)!);
        graphReader = new CommitGraphReader(".git/objects", objectResolver, fileSystem, fileSystem.CreateOffsetReader);
        using var connection = CreateProviderUsingFakeFileSystem(ref fileSystem, configReader, objectResolver).Invoke(".git");

        // Act
        var commit = await ((GitConnectionInternal)connection).GetCommittishAsync<LogEntry>("HEAD^3", e => e.ParentIds);

        // Assert
        commit.Id.ToString().Should().Be("a28d9681fdf40631632a42b303be274e3869d5d5");
    }

    [Test]
    public async Task ReadStash()
    {
        // Arrange
        var source = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepositoryWithStash), source, overwriteFiles: true);
        var provider = CreateProvider(out var services);
        using var connection = provider.Invoke($"{source}/.git");

        // Act
        var stash = (await connection.GetStashesAsync()).Single();
        var changes = await stash.GetChangesAsync(includeUntracked: true);

        // Assert
        using (new AssertionScope())
        {
            stash.Id.ToString().Should().Be("c0b174e8828f8bf4a15289ff64f2126646058973");
            stash.Message.Should().Be("On main: Stash message");
            changes.Should().HaveCount(2);
            changes[0].Type.Should().Be(ChangeType.Removed);
            changes[0].OldPath!.ToString().Should().Be("a.txt");
            changes[1].Type.Should().Be(ChangeType.Added);
            changes[1].NewPath!.ToString().Should().Be("b.txt");
        }
    }

    [Test]
    public void ThrowsNotSupportedExceptionForPromisorPacks()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddDirectory(".git/objects/pack");
        fileSystem.AddFile(".git/objects/pack/pack-123456789abcdef.promisor", new MockFileData(""));
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 0
                bare = false
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Promisor Packs (partial clone)");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForAlternateObjectDBs()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddDirectory(".git/objects/info");
        fileSystem.AddFile(".git/objects/info/alternates", new MockFileData("/path/to/alternate/objects\n"));
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 0
                bare = false
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Alternate Object DBs");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForHttpAlternateObjectDBs()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddDirectory(".git/objects/info");
        fileSystem.AddFile(".git/objects/info/http-alternates", new MockFileData("http://example.com/objects\n"));
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 0
                bare = false
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("HTTP Alternate Object DBs");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForReftableInConfig()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 1
                bare = false
            [extensions]
                refstorage = reftable
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Reftable reference storage");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForReftableDirectory()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddDirectory(".git/reftable");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 0
                bare = false
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Reftable reference storage");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForUnsupportedRepositoryVersion()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 2
                bare = false
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Repository format version 2 (only version 0 and 1 are supported)");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForUnsupportedObjectFormat()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 1
                bare = false
            [extensions]
                objectformat = sha256
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Object format 'sha256' (only SHA-1 is supported)");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForWorktreeConfig()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 1
                bare = false
            [extensions]
                worktreeconfig = true
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Worktree-specific configuration");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForPartialClone()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 1
                bare = false
            [extensions]
                partialclone = origin
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("Partial clone with remote 'origin'");
    }

    [Test]
    public void ThrowsNotSupportedExceptionForMultipleUnsupportedFeatures()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddDirectory(".git/objects/pack");
        fileSystem.AddDirectory(".git/objects/info");
        fileSystem.AddFile(".git/objects/pack/pack-123456789abcdef.promisor", new MockFileData(""));
        fileSystem.AddFile(".git/objects/info/alternates", new MockFileData("/path/to/alternate/objects\n"));
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 1
                bare = false
            [extensions]
                refstorage = reftable
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git"));
        exception.Message.Should().Contain("unsupported Git features:");
        exception.Message.Should().Contain("Promisor Packs (partial clone)");
        exception.Message.Should().Contain("Alternate Object DBs");
        exception.Message.Should().Contain("Reftable reference storage");
    }

    [Test]
    public void DoesNotThrowForSupportedRepositoryFormat()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/config", new MockFileData("""
            [core]
                repositoryformatversion = 1
                bare = false
            [extensions]
                objectformat = sha1
            [user]
                name = Test User
                email = test@example.com
            """));

        // Act & Assert - Should not throw
        using var connection = CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git");
        connection.Should().NotBeNull();
    }

    [Test]
    public void DoesNotThrowForBasicRepository()
    {
        // Arrange
        var fileSystem = default(MockFileSystem);
        
        // Act & Assert - Should not throw (uses the standard fake filesystem)
        using var connection = CreateProviderUsingFakeFileSystem(ref fileSystem).Invoke(".git");
        connection.Should().NotBeNull();
    }

    [Test]
    public async Task ReadsMultiPackIndex()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.MultiPackRepository), folder, overwriteFiles: true);
        using var sut = CreateProvider().Invoke(folder);
        var objects = (ObjectResolver)sut.Objects;

        // Act
        var tip = await sut.GetCommittishAsync("master");

        // Act, Assert
        using (new AssertionScope())
        {
            tip.Id.ToString().Should().Be("aeaa457a27fa39a6017f6da2ca3a51b0a9c54282");
            objects.PackManager.Indices.Should().HaveCount(1);
            objects.PackManager.Indices.First().Should().BeOfType<PackIndexReader.MultiPack>();
        }
    }
}
