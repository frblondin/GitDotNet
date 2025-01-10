using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace GitDotNet.Tests.Data;

public class BlobEntryTests
{
    [Test]
    public void ShouldIdentifyTextBlob()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("This is a text blob.");

        // Act
        var blobEntry = new BlobEntry(hash, data, _ => throw new NotImplementedException());

        // Assert
        blobEntry.IsText.Should().BeTrue();
        blobEntry.GetText().Should().Be("This is a text blob.");
    }

    [Test]
    public void ShouldIdentifyNonTextBlob()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);

        // Act
        var blobEntry = new BlobEntry(hash, [0xFF, 0xFE, 0xFD, 0x00], _ => throw new NotImplementedException());

        // Assert
        blobEntry.IsText.Should().BeFalse();
        blobEntry.GetText().Should().BeNull();
    }
}
