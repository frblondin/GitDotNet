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
    public async Task CheckForNonAmbiguousHashDoesNotThrow()
    {
        // Arrange
        var hashId = new HashId([254, 232, 75, 85]);

        // Act, Assert
        await sut.GetIndexOfAsync(hashId);
    }

    private PackIndexReader sut;

    [SetUp]
    public void Setup()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/packs/data.pack", new MockFileData(Resource.Pack));
        fileSystem.AddFile(".git/objects/packs/data.idx", new MockFileData(Resource.PackIndex));
        var objects = A.Fake<ObjectResolver>(o => o.WithArgumentsForConstructor(() =>
            new(".git", true,
                Options.Create(new IGitConnection.Options()),
                path => new PackManager(path, fileSystem, A.Fake<MultiPackIndexReaderFactory>(), A.Fake<StandardPackIndexReaderFactory>(), null),
                path => CreateLooseReader(path, fileSystem),
                path => CreateLfsReader(path, fileSystem),
                (_, _) => A.Fake<CommitGraphReader>(),
                A.Fake<IMemoryCache>(),
                fileSystem,
                null)));
        sut = new PackIndexReader.Standard(".git/objects/packs/data.idx",
            path => new PackReader(path, fileSystem.CreateOffsetReader),
            fileSystem.CreateOffsetReader,
            fileSystem,
            A.Fake<IMemoryCache>());
    }

    [TearDown]
    public void TearDown()
    {
        sut.Dispose();
    }
}