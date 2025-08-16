using FakeItEasy;
using FluentAssertions;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Readers;

public class PackIndexReaderTests
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
    public async Task CheckForAmbiguousHashDoesNotThrow()
    {
        // Arrange
        var hashId = new HashId([254, 232, 75, 85]);

        // Act, Assert
        await sut.IndexOfAsync(hashId);
    }

    private PackReader sut;

    [SetUp]
    public void Setup()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/packs/data.pack", new MockFileData(Resource.Pack));
        fileSystem.AddFile(".git/objects/packs/data.idx", new MockFileData(Resource.PackIndex));
        var objects = A.Fake<ObjectResolver>(o => o.WithArgumentsForConstructor(() =>
            new(".git", true,
                Options.Create(new GitConnection.Options()),
                path => new PackManager(path, fileSystem, A.Fake<PackReaderFactory>(), null),
                path => CreateLooseReader(path, fileSystem),
                path => CreateLfsReader(path, fileSystem),
                (_, _) => A.Fake<CommitGraphReader>(),
                A.Fake<IMemoryCache>(),
                fileSystem,
                null)));
        sut = new PackReader(fileSystem.CreateOffsetReader(".git/objects/packs/data.pack"),
            async path => await PackIndexReader.LoadAsync(path, fileSystem));
    }

    [TearDown]
    public void TearDown()
    {
        sut.Dispose();
    }
}