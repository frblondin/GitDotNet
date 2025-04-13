using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests;
internal class TreeComparerTests
{
    [Test]
    public async Task ModifiedDetected()
    {
        // Arrange: folder/blob.txt
        var oldBlobEntry = new BlobEntry(oldBlob, Encoding.UTF8.GetBytes("old"), _ => throw new NotImplementedException());
        var newBlobEntry = new BlobEntry(newBlob, Encoding.UTF8.GetBytes("new"), _ => throw new NotImplementedException());
        var old = new TreeEntry(oldTreeRoot, CreateNestedItemData("40000", "folder", oldTreeItem),
            CreateObjectResolver(_ => new TreeEntry(oldTreeItem, CreateNestedItemData("100644", "blob.txt", oldBlob),
                CreateObjectResolver(_ => oldBlobEntry))));
        var @new = new TreeEntry(newTreeRoot, CreateNestedItemData("40000", "folder", newTreeItem),
            CreateObjectResolver(_ => new TreeEntry(newTreeItem, CreateNestedItemData("100644", "blob.txt", newBlob),
                CreateObjectResolver(_ => newBlobEntry))));
        var sut = new TreeComparer(Options.Create(new GitConnection.Options()));

        // Act
        var result = await sut.CompareAsync(old, @new);

        // Assert
        using (new AssertionScope())
        {
            result.Should().HaveCount(1);
            result[0].Type.Should().Be(ChangeType.Modified);
            result[0].OldPath!.ToString().Should().Be("folder/blob.txt");
            result[0].NewPath!.ToString().Should().Be("folder/blob.txt");
            result[0].Old!.Id.Should().BeEquivalentTo(oldBlob);
            result[0].New!.Id.Should().BeEquivalentTo(newBlob);
            var oldEntry = await result[0].Old!.GetEntryAsync<BlobEntry>();
            var newEntry = await result[0].New!.GetEntryAsync<BlobEntry>();
            oldEntry!.GetText().Should().Be("old");
            newEntry!.GetText().Should().Be("new");
        }
    }

    [Test]
    public async Task RemovedDetected()
    {
        // Arrange: folder/blob.txt
        var oldBlobEntry = new BlobEntry(oldBlob, Encoding.UTF8.GetBytes("old"), _ => throw new NotImplementedException());
        var old = new TreeEntry(oldTreeRoot, CreateNestedItemData("40000", "folder", oldTreeItem),
            CreateObjectResolver(_ => new TreeEntry(oldTreeItem, CreateNestedItemData("100644", "blob.txt", oldBlob),
                CreateObjectResolver(_ => oldBlobEntry))));
        var @new = new TreeEntry(newTreeRoot, CreateNestedItemData("40000", "folder", newTreeItem),
            CreateObjectResolver(_ => new TreeEntry(newTreeItem, [],
                CreateObjectResolver(_ => throw new NotImplementedException()))));
        var sut = new TreeComparer(Options.Create(new GitConnection.Options()));

        // Act
        var result = await sut.CompareAsync(old, @new);

        // Assert
        using (new AssertionScope())
        {
            result.Should().HaveCount(1);
            result[0].Type.Should().Be(ChangeType.Removed);
            result[0].OldPath!.ToString().Should().Be("folder/blob.txt");
            result[0].Old!.Id.Should().BeEquivalentTo(oldBlob);
            result[0].New.Should().BeNull();
            var oldEntry = await result[0].Old!.GetEntryAsync<BlobEntry>();
            oldEntry!.GetText().Should().Be("old");
        }
    }

    [Test]
    public async Task AddedDetected()
    {
        // Arrange: folder/blob.txt
        var newBlobEntry = new BlobEntry(newBlob, Encoding.UTF8.GetBytes("new"), _ => throw new NotImplementedException());
        var old = new TreeEntry(oldTreeRoot, CreateNestedItemData("40000", "folder", oldTreeItem),
            CreateObjectResolver(_ => new TreeEntry(oldTreeItem, [],
                CreateObjectResolver(_ => throw new NotImplementedException()))));
        var @new = new TreeEntry(newTreeRoot, CreateNestedItemData("40000", "folder", newTreeItem),
            CreateObjectResolver(_ => new TreeEntry(newTreeItem, CreateNestedItemData("100644", "blob.txt", newBlob),
                CreateObjectResolver(_ => newBlobEntry))));
        var sut = new TreeComparer(Options.Create(new GitConnection.Options()));

        // Act
        var result = await sut.CompareAsync(old, @new);

        // Assert
        using (new AssertionScope())
        {
            result.Should().HaveCount(1);
            result[0].Type.Should().Be(ChangeType.Added);
            result[0].NewPath!.ToString().Should().Be("folder/blob.txt");
            result[0].Old.Should().BeNull();
            result[0].New!.Id.Should().Be(newBlob);
            var newEntry = await result[0].New!.GetEntryAsync<BlobEntry>();
            newEntry!.GetText().Should().Be("new");
        }
    }

    [Test]
    public async Task RenamedDetectedIfAboveRenameThreshold()
    {
        // Arrange: folder/blob.txt renamed to folder/renamedBlob.txt
        var oldBlobEntry = new BlobEntry(oldBlob,
                                         Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 100))),
                                         _ => throw new NotImplementedException());
        var newBlobEntry = new BlobEntry(newBlob,
                                         Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 105))),
                                         _ => throw new NotImplementedException());
        var old = new TreeEntry(oldTreeRoot, CreateNestedItemData("40000", "folder", oldTreeItem),
            CreateObjectResolver(_ => new TreeEntry(oldTreeItem, CreateNestedItemData("100644", "blob.txt", oldBlob),
                CreateObjectResolver(_ => oldBlobEntry))));
        var @new = new TreeEntry(newTreeRoot, CreateNestedItemData("40000", "folder", newTreeItem),
            CreateObjectResolver(_ => new TreeEntry(newTreeItem, CreateNestedItemData("100644", "renamedBlob.txt", newBlob),
                CreateObjectResolver(_ => newBlobEntry))));
        var sut = new TreeComparer(Options.Create(new GitConnection.Options()));

        // Act
        var result = await sut.CompareAsync(old, @new);

        // Assert
        using (new AssertionScope())
        {
            result.Should().HaveCount(1);
            result[0].Type.Should().Be(ChangeType.Renamed);
            result[0].OldPath!.ToString().Should().Be("folder/blob.txt");
            result[0].NewPath!.ToString().Should().Be("folder/renamedBlob.txt");
            result[0].Old!.Id.Should().BeEquivalentTo(oldBlob);
            result[0].New!.Id.Should().BeEquivalentTo(newBlob);
        }
    }

    [Test]
    public async Task RenamedIgnoredIfBelowRenameThreshold()
    {
        // Arrange: folder/blob.txt renamed to folder/renamedBlob.txt
        var oldBlobEntry = new BlobEntry(oldBlob,
                                         Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("0", 100))),
                                         _ => throw new NotImplementedException());
        var newBlobEntry = new BlobEntry(newBlob,
                                         Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("0", 150))),
                                         _ => throw new NotImplementedException());
        var old = new TreeEntry(oldTreeRoot, CreateNestedItemData("40000", "folder", oldTreeItem),
            CreateObjectResolver(_ => new TreeEntry(oldTreeItem, CreateNestedItemData("100644", "blob.txt", oldBlob),
                CreateObjectResolver(_ => oldBlobEntry))));
        var @new = new TreeEntry(newTreeRoot, CreateNestedItemData("40000", "folder", newTreeItem),
            CreateObjectResolver(_ => new TreeEntry(newTreeItem, CreateNestedItemData("100644", "renamedBlob.txt", newBlob),
                CreateObjectResolver(_ => newBlobEntry))));
        var sut = new TreeComparer(Options.Create(new GitConnection.Options()));

        // Act
        var result = await sut.CompareAsync(old, @new);

        // Assert
        using (new AssertionScope())
        {
            result.Should().HaveCount(2);
            result.Should().Contain(c => c.Type == ChangeType.Added);
            result.Should().Contain(c => c.Type == ChangeType.Removed);
        }
    }

    private static byte[] CreateNestedItemData(string mode, string name, HashId id)
    {
        var result = new List<byte>();
        result.AddRange(Encoding.UTF8.GetBytes(mode));
        result.Add(0x20);
        result.AddRange(Encoding.UTF8.GetBytes(name));
        result.Add(0x00);
        result.AddRange(id.Hash);
        return [.. result];
    }

    private HashId oldTreeRoot, oldTreeItem, oldBlob, newTreeRoot, newTreeItem, newBlob;

    [SetUp]
    public void SetUp()
    {
        oldTreeRoot = RandomNumberGenerator.GetBytes(20);
        oldTreeItem = RandomNumberGenerator.GetBytes(20);
        oldBlob = RandomNumberGenerator.GetBytes(20);
        newTreeRoot = RandomNumberGenerator.GetBytes(20);
        newTreeItem = RandomNumberGenerator.GetBytes(20);
        newBlob = RandomNumberGenerator.GetBytes(20);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
    }
}
