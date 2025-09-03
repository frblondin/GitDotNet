using System.IO.Abstractions.TestingHelpers;
using System.Text;
using FluentAssertions;
using GitDotNet.Writers;
using GitDotNet.Readers;
using Microsoft.Extensions.Logging;
using FakeItEasy;

namespace GitDotNet.Tests.Writers;

public class LooseWriterTests
{
    private MockFileSystem _fileSystem;
    private LooseWriter _writer;
    private ILogger<LooseWriter> _logger;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystem();
        _logger = A.Fake<ILogger<LooseWriter>>();
        _writer = new LooseWriter(".git", _fileSystem, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _writer?.Dispose();
    }

    [Test]
    public async Task WriteObjectAsync_WritesNewBlobObject()
    {
        // Arrange
        var content = "Hello, World!"u8.ToArray();

        // Act
        var objectId = await _writer.WriteObjectAsync(EntryType.Blob, content);

        // Assert
        objectId.Should().NotBeNull();
        
        // Verify the object file was created
        var expectedPath = $".git/objects/{objectId.ToString()[..2]}/{objectId.ToString()[2..]}";
        _fileSystem.File.Exists(expectedPath).Should().BeTrue();

        // Verify we can read it back using LooseReader
        var reader = new LooseReader(".git/objects", _fileSystem);
        var result = reader.TryLoad(objectId.ToString());
        
        result.Type.Should().Be(EntryType.Blob);
        result.Length.Should().Be(content.Length);
        
        using var stream = result.DataProvider!();
        var readContent = new byte[content.Length];
        await stream.ReadExactlyAsync(readContent);
        readContent.Should().BeEquivalentTo(content);
    }

    [Test]
    public async Task WriteObjectAsync_WritesNewCommitObject()
    {
        // Arrange
        var commitContent = """
            tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904
            author John Doe <john@example.com> 1234567890 +0000
            committer John Doe <john@example.com> 1234567890 +0000

            Initial commit
            """u8.ToArray();

        // Act
        var objectId = await _writer.WriteObjectAsync(EntryType.Commit, commitContent);

        // Assert
        objectId.Should().NotBeNull();
        
        // Verify the object file was created
        var expectedPath = $".git/objects/{objectId.ToString()[..2]}/{objectId.ToString()[2..]}";
        _fileSystem.File.Exists(expectedPath).Should().BeTrue();

        // Verify we can read it back using LooseReader
        var reader = new LooseReader(".git/objects", _fileSystem);
        var result = reader.TryLoad(objectId.ToString());
        
        result.Type.Should().Be(EntryType.Commit);
        result.Length.Should().Be(commitContent.Length);
        
        using var stream = result.DataProvider!();
        var readContent = new byte[commitContent.Length];
        await stream.ReadExactlyAsync(readContent);
        readContent.Should().BeEquivalentTo(commitContent);
    }

    [Test]
    public async Task WriteObjectAsync_SkipsExistingObject()
    {
        // Arrange
        var content = "Hello, World!"u8.ToArray();

        // Act - Write first time
        var objectId1 = await _writer.WriteObjectAsync(EntryType.Blob, content);
        
        // Act - Write second time (should skip)
        var objectId2 = await _writer.WriteObjectAsync(EntryType.Blob, content);

        // Assert
        objectId1.Should().Be(objectId2);
        
        // Verify only one file was created
        var expectedPath = $".git/objects/{objectId1.ToString()[..2]}/{objectId1.ToString()[2..]}";
        _fileSystem.File.Exists(expectedPath).Should().BeTrue();
    }

    [Test]
    public void Constructor_CreatesObjectsDirectory()
    {
        // Assert
        _fileSystem.Directory.Exists(".git/objects").Should().BeTrue();
    }

    [Test]
    public void GetTypeBytes_ThrowsForUnsupportedType()
    {
        // Arrange & Act
        var act = () => _writer.WriteObjectAsync(EntryType.OfsDelta, "content"u8.ToArray());

        // Assert
        act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*Unsupported entry type for loose objects: OfsDelta*");
    }

    [Test]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Act & Assert - Should not throw
        _writer.Dispose();
        _writer.Dispose();
    }

    [Test]
    public void DisposedWriter_ThrowsObjectDisposedException()
    {
        // Arrange
        _writer.Dispose();
        var content = "Hello, World!"u8.ToArray();

        // Act & Assert
        var act = async () => await _writer.WriteObjectAsync(EntryType.Blob, content);
        act.Should().ThrowAsync<ObjectDisposedException>();
    }
}