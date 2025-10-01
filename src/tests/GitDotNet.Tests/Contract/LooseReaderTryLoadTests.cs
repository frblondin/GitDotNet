using GitDotNet.Readers;
using NUnit.Framework;
using FluentAssertions;
using FakeItEasy;

namespace GitDotNet.Tests.Contract;

[TestFixture]
public class LooseReaderTryLoadTests
{
    private LooseReader _looseReader = null!;

    [SetUp]
    public void Setup()
    {
        _looseReader = A.Fake<LooseReader>();
    }

    [Test]
    public void TryLoad_WithValidCommitHash_ShouldReturnCommitTypeAndDataProvider()
    {
        // Arrange
        var hexString = "a1b2c3d4e5f6789012345678901234567890abcd";
        var expectedLength = 256L;
        var dataProvider = A.Fake<Func<Stream>>();
        
        A.CallTo(() => _looseReader.TryLoad(hexString))
            .Returns((EntryType.Commit, dataProvider, expectedLength));

        // Act
        var (type, provider, length) = _looseReader.TryLoad(hexString);

        // Assert
        type.Should().Be(EntryType.Commit);
        provider.Should().NotBeNull();
        length.Should().Be(expectedLength);
    }

    [Test]
    public void TryLoad_WithNonExistentHash_ShouldReturnDefaultValues()
    {
        // Arrange
        var hexString = "0000000000000000000000000000000000000000";
        
        A.CallTo(() => _looseReader.TryLoad(hexString))
            .Returns((default, default, -1));

        // Act
        var (type, provider, length) = _looseReader.TryLoad(hexString);

        // Assert
        type.Should().Be(default);
        provider.Should().BeNull();
        length.Should().Be(-1);
    }

    [Test]
    public void TryLoad_WithAmbiguousPartialHash_ShouldThrowAmbiguousHashException()
    {
        // Arrange
        var hexString = "aaaa";
        
        A.CallTo(() => _looseReader.TryLoad(hexString))
            .Throws<AmbiguousHashException>();

        // Act & Assert
        var act = () => _looseReader.TryLoad(hexString);
        act.Should().Throw<AmbiguousHashException>();
    }

    [Test]
    public void TryLoad_WithCorruptedObject_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var hexString = "corrupted123456789012345678901234567890";
        
        A.CallTo(() => _looseReader.TryLoad(hexString))
            .Throws<InvalidOperationException>();

        // Act & Assert
        var act = () => _looseReader.TryLoad(hexString);
        act.Should().Throw<InvalidOperationException>();
    }
}
