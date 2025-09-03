using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Readers;

public class PackReaderTests
{
    [Test]
    public async Task GetBlobContent()
    {
        // Act
        var entry = await sut.GetAsync(int.Parse(Resource.BlobPosition), HashId.Empty, _ => throw new NotImplementedException());
        var text = Encoding.UTF8.GetString(entry.Data).ReplaceLineEndings();

        // Assert
        text.Should().Be(Resource.BlobContent.ReplaceLineEndings());
    }

    [Test]
    public async Task GetOfsDeltaContent()
    {
        // Act
        var entry = await sut.GetAsync(int.Parse(Resource.OfsDeltaPosition), HashId.Empty, _ => throw new NotImplementedException());
        var text = Encoding.UTF8.GetString(entry.Data).ReplaceLineEndings();

        // Assert
        text.Should().Be(Resource.OfsDeltaContent.ReplaceLineEndings());
    }

    [Test]
    public async Task GetCommits()
    {
        // Act
        var objects = new ObjectResolver(".git", true,
            Options.Create(new IGitConnection.Options()),
            path => new PackManager(path, fileSystem, _ => sut),
            path => CreateLooseReader(path, fileSystem),
            path => CreateLfsReader(path, fileSystem),
            (_, _) => throw new NotImplementedException(),
            A.Fake<IMemoryCache>(),
            fileSystem);
        var entries = sut.GetEntriesAsync(_ => null!);
        var firstCommitEntry = await entries.FirstAsync(e => e.Type == EntryType.Commit);

        // Assert
        using (new AssertionScope())
        {
            var commit = (CommitEntry)objects.CreateEntry(firstCommitEntry);
            commit.Type.Should().Be(EntryType.Commit);
            commit.Id.ToString().Should().Be("fee84b5575de791d1ac1edb089a63ab85d504f3c");
            commit.Message.Should().Be("20c4b2e9-c474-481a-88d1-e60d297d1edb");
            (await commit.GetParentsAsync()).Should().HaveCount(0);
            commit.Author!.Name.Should().Be("Rosemary15");
            commit.Author.Email.Should().Be("Randi.Blanda42@hotmail.com");
            commit.Author.Timestamp.Should().Be(new DateTime(2025, 1, 3, 16, 6, 16, DateTimeKind.Utc));
            commit.Committer!.Name.Should().Be("Rosemary15");
            commit.Committer.Email.Should().Be("Randi.Blanda42@hotmail.com");
            commit.Committer.Timestamp.Should().Be(new DateTime(2025, 1, 3, 16, 6, 16, DateTimeKind.Utc));
        }
    }

    private PackReader sut;
    private MockFileSystem fileSystem;

    [SetUp]
    public void Setup()
    {
        fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/packs/data.pack", new MockFileData(Resource.Pack));
        fileSystem.AddFile(".git/objects/packs/data.idx", new MockFileData(Resource.PackIndex));
        var objects = A.Fake<ObjectResolver>(o => o.WithArgumentsForConstructor(() =>
            new(".git", true,
                Options.Create(new IGitConnection.Options()),
                path => new PackManager(path, fileSystem, A.Fake<PackReaderFactory>(), null),
                path => CreateLooseReader(path, fileSystem),
                path => CreateLfsReader(path, fileSystem),
                (_, _) => A.Fake<CommitGraphReader>(),
                A.Fake<IMemoryCache>(),
                fileSystem,
                null)));
        sut = new PackReader(".git/objects/packs/data.pack",
            fileSystem.CreateOffsetReader,
            async path => await PackIndexReader.LoadAsync(path, fileSystem));
    }

    [TearDown]
    public void TearDown()
    {
        sut.Dispose();
    }
}