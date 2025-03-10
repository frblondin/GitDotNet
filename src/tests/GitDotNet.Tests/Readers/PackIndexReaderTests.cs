using FakeItEasy;
using FluentAssertions;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using Microsoft.Extensions.Caching.Memory;
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

    private PackReader sut;

    [SetUp]
    public void Setup()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/packs/data.pack", new MockFileData(Resource.Pack));
        fileSystem.AddFile(".git/objects/packs/data.idx", new MockFileData(Resource.PackIndex));
        var objects = A.Fake<Objects>(o => o.WithArgumentsForConstructor(() =>
            new(".git", true,
                Options.Create(new GitConnection.Options()),
                path => CreateLooseReader(path, fileSystem),
                A.Fake<PackReaderFactory>(),
                path => CreateLfsReader(path, fileSystem),
                (_, _) => A.Fake<CommitGraphReader>(),
                A.Fake<IMemoryCache>(),
                fileSystem)));
        sut = new PackReader(fileSystem.CreateOffsetReader(".git/objects/packs/data.pack"),
                             Options.Create(new GitConnection.Options()),
                             async path => await PackIndexReader.LoadAsync(path, fileSystem),
                             A.Fake<IMemoryCache>());
    }

    [TearDown]
    public void TearDown()
    {
        sut.Dispose();
    }
}