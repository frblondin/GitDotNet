using GitDotNet.Readers;
using NUnit.Framework;
using FluentAssertions;
using System.IO;
using FakeItEasy;

namespace GitDotNet.Tests.Contract;

[TestFixture]
public class PackReaderExtractOffsetTests
{
    [Test]
    public void ExtractOffset_WithSingleByte_ShouldReturnCorrectValue()
    {
        // Arrange
        var data = new byte[] { 0x7F }; // 127, MSB not set
        using var stream = new MemoryStream(data);

        // Act
        var result = PackReader.ExtractOffset(stream);

        // Assert
        result.Should().Be(127);
    }

    [Test]
    public void ExtractOffset_WithMultipleBytes_ShouldReturnCorrectValue()
    {
        // Arrange
        var data = new byte[] { 0x80, 0x01 }; // MSB set in first byte
        using var stream = new MemoryStream(data);

        // Act
        var result = PackReader.ExtractOffset(stream);

        // Assert
        result.Should().BeGreaterThan(0);
    }

    [Test]
    public void ExtractOffset_WithEndOfStream_ShouldThrowEndOfStreamException()
    {
        // Arrange
        using var stream = new MemoryStream(new byte[0]);

        // Act & Assert
        var act = () => PackReader.ExtractOffset(stream);
        act.Should().Throw<EndOfStreamException>();
    }
}
