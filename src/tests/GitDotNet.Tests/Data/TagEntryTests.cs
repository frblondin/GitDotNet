using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Data;
public class TagEntryTests
{
    [Test]
    public async Task ShouldParseCorrectly()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("object 1234567890abcdef\n" +
                                          "type commit\n" +
                                          "tag v1.0\n" +
                                          "tagger John Doe <john.doe@example.com> 1627846267 +0200\n" +
                                          "\n" +
                                          "Initial release");

        // Act
        var tagEntry = new TagEntry(hash, data, CreateObjectResolver(h => new MockCommitEntry(h)));

        // Assert
        using (new AssertionScope())
        {
            tagEntry.TargetType.Should().Be(EntryType.Commit);
            tagEntry.Tag.Should().Be("v1.0");
            tagEntry.Tagger.Should().NotBeNull();
            tagEntry.Tagger?.Name.Should().Be("John Doe");
            tagEntry.Tagger?.Email.Should().Be("john.doe@example.com");
            tagEntry.Message.Should().Be("Initial release");

            var target = await tagEntry.GetTargetAsync();
            target.Should().BeOfType<MockCommitEntry>();
        }
    }

    [Test]
    public void ShouldThrowWhenObjectIsMissing()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("type commit\n" +
                                          "tag v1.0\n" +
                                          "tagger John Doe <john.doe@example.com> 1627846267 +0200\n" +
                                          "\n" +
                                          "Initial release");

        // Act & Assert
        var act = () => new TagEntry(hash, data, CreateObjectResolver(h => new MockCommitEntry(h))).TargetType;
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid tag entry: missing object.");
    }

    [Test]
    public void ShouldThrowWhenTypeIsMissing()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("object 1234567890abcdef\n" +
                                          "tag v1.0\n" +
                                          "tagger John Doe <john.doe@example.com> 1627846267 +0200\n" +
                                          "\n" +
                                          "Initial release");

        // Act & Assert
        var act = () => new TagEntry(hash, data, CreateObjectResolver(h => new MockCommitEntry(h))).TargetType;
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid tag entry: missing type.");
    }

    [Test]
    public void ShouldThrowWhenTagIsMissing()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("object 1234567890abcdef\n" +
                                          "type commit\n" +
                                          "tagger John Doe <john.doe@example.com> 1627846267 +0200\n" +
                                          "\n" +
                                          "Initial release");

        // Act & Assert
        var act = () => new TagEntry(hash, data, CreateObjectResolver(h => new MockCommitEntry(h))).TargetType;
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid tag entry: missing tag.");
    }

    private class MockCommitEntry(HashId Id) : Entry(EntryType.Commit, Id, []) { }
}