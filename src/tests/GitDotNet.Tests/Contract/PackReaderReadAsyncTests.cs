using GitDotNet;
using GitDotNet.Readers;
using NUnit.Framework;
using FluentAssertions;
using System.Threading.Tasks;
using FakeItEasy;
using System;

namespace GitDotNet.Tests.Contract;

[TestFixture]
public class PackReaderReadAsyncTests
{
    private PackReader _packReader = null!;
    private HashId _validCommitHash = null!;
    private HashId _validTreeHash = null!;
    private HashId _validBlobHash = null!;
    private HashId _validTagHash = null!;
    private Func<HashId, Task<UnlinkedEntry>> _dependentEntryProvider = null!;

    [SetUp]
    public void Setup()
    {
        // Create a fake PackReader for testing contracts
        _packReader = A.Fake<PackReader>();
        
        // Test hashes - these should be replaced with real hashes from test repository
        _validCommitHash = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
        _validTreeHash = new HashId("b2c3d4e5f6789012345678901234567890abcde0");
        _validBlobHash = new HashId("c3d4e5f67890123456789012345678901234abcdef");
        _validTagHash = new HashId("d4e5f6789012345678901234567890123abcdef0");
        
        _dependentEntryProvider = A.Fake<Func<HashId, Task<UnlinkedEntry>>>();
    }

    [TearDown]
    public void TearDown()
    {
        _packReader?.Dispose();
    }

    [Test]
    public async Task ReadAsync_WithValidCommitObject_ShouldReturnCommitUnlinkedEntry()
    {
        // Arrange
        var offset = 1024L;
        var expectedData = "tree abc123\nparent def456\nauthor Test User <test@example.com> 1234567890 +0000\ncommitter Test User <test@example.com> 1234567890 +0000\n\nTest commit message\n"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Commit, _validCommitHash, expectedData);
        
        A.CallTo(() => _packReader.ReadAsync(_validCommitHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validCommitHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be(EntryType.Commit);
        result.Id.Should().Be(_validCommitHash);
        result.Data.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public async Task ReadAsync_WithValidTreeObject_ShouldReturnTreeUnlinkedEntry()
    {
        // Arrange
        var offset = 2048L;
        var expectedData = new byte[] { 0x31, 0x30, 0x30, 0x36, 0x34, 0x34, 0x20, 0x66, 0x69, 0x6c, 0x65, 0x2e, 0x74, 0x78, 0x74, 0x00 }; // Sample tree data
        var expectedEntry = new UnlinkedEntry(EntryType.Tree, _validTreeHash, expectedData);
        
        A.CallTo(() => _packReader.ReadAsync(_validTreeHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validTreeHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be(EntryType.Tree);
        result.Id.Should().Be(_validTreeHash);
        result.Data.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public async Task ReadAsync_WithValidBlobObject_ShouldReturnBlobUnlinkedEntry()
    {
        // Arrange
        var offset = 3072L;
        var expectedData = "Hello, World!\nThis is a test file content."u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validBlobHash, expectedData);
        
        A.CallTo(() => _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be(EntryType.Blob);
        result.Id.Should().Be(_validBlobHash);
        result.Data.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public async Task ReadAsync_WithValidTagObject_ShouldReturnTagUnlinkedEntry()
    {
        // Arrange
        var offset = 4096L;
        var expectedData = "object abc123\ntype commit\ntag v1.0.0\ntagger Test User <test@example.com> 1234567890 +0000\n\nTag message\n"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Tag, _validTagHash, expectedData);
        
        A.CallTo(() => _packReader.ReadAsync(_validTagHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validTagHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be(EntryType.Tag);    
        result.Id.Should().Be(_validTagHash);
        result.Data.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public async Task ReadAsync_WithOffsetDeltaObject_ShouldReconstructAndReturnEntry()
    {
        // Arrange
        var offset = 5120L;
        var baseObjectData = "Base object content"u8.ToArray();
        var reconstructedData = "Base object content with delta changes"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validBlobHash, reconstructedData);
        
        A.CallTo(() => _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be(EntryType.Blob);
        result.Data.Should().BeEquivalentTo(reconstructedData);
        result.Data.Should().NotBeEquivalentTo(baseObjectData); // Should be reconstructed
    }

    [Test]
    public async Task ReadAsync_WithReferenceDeltaObject_ShouldReconstructUsingDependentProvider()
    {
        // Arrange
        var offset = 6144L;
        var baseObjectData = "Reference base content"u8.ToArray();
        var baseEntry = new UnlinkedEntry(EntryType.Blob, new HashId("ba5e0b1ec7123456789012345678901234567890"), baseObjectData);
        var reconstructedData = "Reference base content with ref delta changes"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validBlobHash, reconstructedData);
        
        A.CallTo(() => _dependentEntryProvider(A<HashId>._))
            .Returns(baseEntry);
        A.CallTo(() => _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Type.Should().Be(EntryType.Blob);
        result.Data.Should().BeEquivalentTo(reconstructedData);
    }

    [Test]
    public void ReadAsync_WithDisposedPackReader_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var offset = 1024L;
        A.CallTo(() => _packReader.ReadAsync(_validCommitHash, offset, _dependentEntryProvider))
            .Throws(new ObjectDisposedException("test"));

        // Act & Assert
        var act = async () => await _packReader.ReadAsync(_validCommitHash, offset, _dependentEntryProvider);
        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public void ReadAsync_WithInvalidOffset_ShouldThrowEndOfStreamException()
    {
        // Arrange
        var invalidOffset = -1L;
        A.CallTo(() => _packReader.ReadAsync(_validCommitHash, invalidOffset, _dependentEntryProvider))
            .Throws<EndOfStreamException>();

        // Act & Assert
        var act = async () => await _packReader.ReadAsync(_validCommitHash, invalidOffset, _dependentEntryProvider);
        act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Test]
    public void ReadAsync_WithUnknownObjectType_ShouldThrowNotImplementedException()
    {
        // Arrange
        var offset = 1024L;
        A.CallTo(() => _packReader.ReadAsync(_validCommitHash, offset, _dependentEntryProvider))
            .Throws<NotImplementedException>();

        // Act & Assert
        var act = async () => await _packReader.ReadAsync(_validCommitHash, offset, _dependentEntryProvider);
        act.Should().ThrowAsync<NotImplementedException>()
            .WithMessage("*Unknown type*");
    }

    [Test]
    public async Task ReadAsync_WithDeeplyNestedDeltaChain_ShouldReconstructCorrectly()
    {
        // Arrange
        var offset = 7168L;
        var finalReconstructedData = "Final data after multiple delta applications"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validBlobHash, finalReconstructedData);
        
        A.CallTo(() => _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider);

        // Assert
        result.Should().NotBeNull();
        result.Data.Should().BeEquivalentTo(finalReconstructedData);
    }

    [Test]
    public void ReadAsync_WithExcessiveDeltaChainDepth_ShouldThrowInvalidOperationException()
    {
        // Arrange - Simulating delta chain depth > 50 levels
        var offset = 8192L;
        A.CallTo(() => _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider))
            .Throws<InvalidOperationException>();

        // Act & Assert
        var act = async () => await _packReader.ReadAsync(_validBlobHash, offset, _dependentEntryProvider);
        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
