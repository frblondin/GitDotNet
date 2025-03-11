using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Tools;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Data;

public class TreeEntryTests
{
    [Test]
    public void ShouldParseTreeEntryCorrectly()
    {
        // Arrange
        var hash = new HashId([0x01, 0x02, 0x03, 0x04]);
        var data = CreateData((100644, "file.txt", "fee84b5575de791d1ac1edb089a63ab85d504f3c"),
                              (040000, "dir", "efe84b5575de791d1ac1edb089a63ab85d504f3c")
        );
        Func<HashId, Entry> objectResolver = objHash => objHash.ToString() switch
        {
            "fee84b5575de791d1ac1edb089a63ab85d504f3c" => new MockBlobEntry(objHash),
            "efe84b5575de791d1ac1edb089a63ab85d504f3c" => new MockTreeEntry(objHash),
            _ => throw new InvalidOperationException("Invalid object hash.")
        };

        // Act
        var treeEntry = new TreeEntry(hash, data, CreateObjectResolver(objectResolver));

        // Assert
        using (new AssertionScope())
        {
            treeEntry.Children.Should().HaveCount(2);

            var file = treeEntry.Children[0];
            file.Mode.EntryType.Should().Be(EntryType.Blob);
            file.Name.Should().Be("file.txt");
            file.Id.ToString().Should().Be("fee84b5575de791d1ac1edb089a63ab85d504f3c");

            var dir = treeEntry.Children[1];
            dir.Mode.EntryType.Should().Be(EntryType.Tree);
            dir.Name.Should().Be("dir");
            dir.Id.ToString().Should().Be("efe84b5575de791d1ac1edb089a63ab85d504f3c");
        }
    }

    private static byte[] CreateData(params (int mode, string name, string hash)[] entries)
    {
        var data = new List<byte>();
        foreach (var (mode, name, hash) in entries)
        {
            data.AddRange(Encoding.ASCII.GetBytes($"{mode} "));
            data.AddRange(Encoding.UTF8.GetBytes(name));
            data.Add(0x00); // Null terminator
            data.AddRange(hash.HexToByteArray());
        }
        return [.. data];
    }

    private record class MockBlobEntry(HashId Id) : BlobEntry(Id, [], _ => throw new NotImplementedException()) { }
    private record class MockTreeEntry(HashId Id) : TreeEntry(Id, [], CreateObjectResolver(h => new MockTreeEntry(h))) { }
}
