using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Tools;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Data;

public class CommitEntryTests
{
    [Test]
    public async Task ShouldParseCorrectly()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("tree 1234567890abcdef\n" +
                                          "parent abcdef1234567890\n" +
                                          "author John Doe <john.doe@example.com> 1627846267 +0200\n" +
                                          "committer Jane Doe <jane.doe@example.com> 1627846267 +0200\n" +
                                          "\n" +
                                          "Initial commit");

        // Act
        var commitEntry = new CommitEntry(hash, data, CreateObjectResolver(h => h.ToString() switch
        {
            "1234567890abcdef" => new MockTreeEntry(h),
            "abcdef1234567890" => new MockCommitEntry(h),
            _ => throw new InvalidOperationException("Invalid object hash.")
        }));

        // Assert
        using (new AssertionScope())
        {
            commitEntry.Author.Should().NotBeNull();
            commitEntry.Author?.Name.Should().Be("John Doe");
            commitEntry.Author?.Email.Should().Be("john.doe@example.com");
            commitEntry.Committer.Should().NotBeNull();
            commitEntry.Committer?.Name.Should().Be("Jane Doe");
            commitEntry.Committer?.Email.Should().Be("jane.doe@example.com");
            commitEntry.Message.Should().Be("Initial commit");

            var tree = await commitEntry.GetRootTreeAsync();
            tree.Should().BeOfType<MockTreeEntry>();

            var parents = await commitEntry.GetParentsAsync();
            parents.Should().HaveCount(1);
            parents[0].Should().BeOfType<MockCommitEntry>();
        }
    }

    [Test]
    public void ShouldThrowWhenTreeIsMissing()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = Encoding.UTF8.GetBytes("parent abcdef1234567890\n" +
                                          "author John Doe <john.doe@example.com> 1627846267 +0200\n" +
                                          "committer Jane Doe <jane.doe@example.com> 1627846267 +0200\n" +
                                          "\n" +
                                          "Initial commit");

        // Act & Assert
        var act = () => new CommitEntry(hash, data, CreateObjectResolver(h => throw new NotImplementedException())).Author;
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid commit entry: missing tree.");
    }

    private record class MockTreeEntry(HashId Id) : TreeEntry(Id, [], CreateObjectResolver(h => new MockTreeEntry(h))) { }
    private record class MockCommitEntry(HashId Id) : CommitEntry(Id, [], CreateObjectResolver(h => new MockTreeEntry(h))) { }
}
