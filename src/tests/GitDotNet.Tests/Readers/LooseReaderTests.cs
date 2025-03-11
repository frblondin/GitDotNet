using FluentAssertions;
using GitDotNet.Readers;
using GitDotNet.Tests.Properties;
using System.IO.Abstractions.TestingHelpers;

namespace GitDotNet.Tests.Readers;

public class LooseReaderTests
{
    [Test]
    public void ReadsFileData()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/objects/01/23456789abcdef0123456789abcdef01234567", new MockFileData(Resource.LooseBlobData));
        var sut = new LooseReader(".git/objects", fileSystem);

        // Act
        var entry = sut.TryLoad("0123456789abcdef0123456789abcdef01234567");

        // Assert
        entry.Type.Should().Be(EntryType.Blob);
        using var reader = new StreamReader(entry.DataProvider!());
        reader.ReadToEnd().Should().Be(Resource.LooseBlobContent);
    }

    [Test]
    public void ReadsFileDataFromAbbreviatedHash()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/objects/01/23456789abcdef0123456789abcdef01234567", new MockFileData(Resource.LooseBlobData));
        var sut = new LooseReader(".git/objects", fileSystem);

        // Act
        var entry = sut.TryLoad("0123456789abcdef");

        // Assert
        entry.Type.Should().Be(EntryType.Blob);
        using var reader = new StreamReader(entry.DataProvider!());
        reader.ReadToEnd().Should().Be(Resource.LooseBlobContent);
    }

    [Test]
    public void ThrowExceptionWhenAmbiguousAbbreviatedHashes()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        fileSystem.AddFile(".git/objects/01/23456789abcdef0123456789abcdef01234567", new MockFileData(Resource.LooseBlobData));
        fileSystem.AddFile(".git/objects/01/23456789abcdef0123456789aaaaaaaaaaaaaa", new MockFileData(Resource.LooseBlobData));
        var sut = new LooseReader(".git/objects", fileSystem);

        // Act
        var readObject = () => sut.TryLoad("0123456789abcdef");

        // Assert
        readObject.Should().Throw<AmbiguousHashException>();
    }
}
