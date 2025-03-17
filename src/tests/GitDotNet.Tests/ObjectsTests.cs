using FakeItEasy;
using FluentAssertions;
using GitDotNet.Readers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests;

public class ObjectsTests
{
    [Test]
    public async Task UsesLooseObjectReaderIfFileExists()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        var looseReader = A.Fake<LooseReader>(o =>
        {
            o.ConfigureFake(fake =>
                A.CallTo(() => fake.TryLoad("0123456789abcdef0123456789abcdef01234567"))
                 .Returns((EntryType.Blob, () => new MemoryStream([42]), 1)));
        });
        var sut = new ObjectResolver(".git", useReadCommitGraph: true,
                              Options.Create(new GitConnection.Options()),
                              _ => looseReader,
                              _ => throw new NotImplementedException(),
                              path => CreateLfsReader(path, fileSystem),
                              (_, _) => throw new NotImplementedException(),
                              A.Fake<IMemoryCache>(),
                              fileSystem);

        // Act
        var entry = await sut.GetAsync<BlobEntry>("0123456789abcdef0123456789abcdef01234567");

        // Assert
        entry.Data.Should().Equal([42]);
    }
}
