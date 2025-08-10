using FluentAssertions;
using NUnit.Framework;

namespace GitDotNet.Tests;

public class TreeEntryItemTests
{
    [Test]
    public void Equals_ShouldReturnTrue_ForSameId()
    {
        // Arrange
        var id = new HashId("fee84b5575de791d1ac1edb089a63ab85d504f3c");
        var item1 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));
        var item2 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));

        // Act
        var result = item1.Equals(item2);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void Equals_ShouldReturnFalse_ForDifferentId()
    {
        // Arrange
        var id1 = new HashId("fee84b5575de791d1ac1edb089a63ab85d504f3c");
        var id2 = new HashId("efe84b5575de791d1ac1edb089a63ab85d504f3c");
        var item1 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id1, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));
        var item2 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id2, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));

        // Act
        var result = item1.Equals(item2);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void GetHashCode_ShouldReturnSameHashCode_ForSameId()
    {
        // Arrange
        var id = new HashId("fee84b5575de791d1ac1edb089a63ab85d504f3c");
        var item1 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));
        var item2 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));

        // Act
        var hashCode1 = item1.GetHashCode();
        var hashCode2 = item2.GetHashCode();

        // Assert
        hashCode1.Should().Be(hashCode2);
    }

    [Test]
    public void GetHashCode_ShouldReturnDifferentHashCode_ForDifferentId()
    {
        // Arrange
        var id1 = new HashId("fee84b5575de791d1ac1edb089a63ab85d504f3c");
        var id2 = new HashId("efe84b5575de791d1ac1edb089a63ab85d504f3c");
        var item1 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id1, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));
        var item2 = new TreeEntryItem(FileMode.RegularFile, "file.txt", id2, (id) => Task.FromResult<Entry>(new MockBlobEntry(id)));

        // Act
        var hashCode1 = item1.GetHashCode();
        var hashCode2 = item2.GetHashCode();

        // Assert
        hashCode1.Should().NotBe(hashCode2);
    }

    private class MockBlobEntry : BlobEntry
    {
        public MockBlobEntry(HashId id) : base(id, Array.Empty<byte>(), _ => throw new NotImplementedException()) { }
    }
}
