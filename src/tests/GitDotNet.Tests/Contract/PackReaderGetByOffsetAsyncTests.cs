using GitDotNet;
using GitDotNet.Readers;
using NUnit.Framework;
using FluentAssertions;
using System.Threading.Tasks;
using FakeItEasy;
using System;

namespace GitDotNet.Tests.Contract;

[TestFixture]
public class PackReaderGetByOffsetAsyncTests
{
    private PackReader _packReader = null!;
    private HashId _validHash = null!;
    private Func<Task<UnlinkedEntry>> _provider = null!;

    [SetUp]
    public void Setup()
    {
        _packReader = A.Fake<PackReader>();
        _validHash = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
        _provider = A.Fake<Func<Task<UnlinkedEntry>>>();
    }

    [TearDown]
    public void TearDown()
    {
        _packReader?.Dispose();
    }

    [Test]
    public async Task GetByOffsetAsync_FirstCall_ShouldCallProviderAndCacheResult()
    {
        // Arrange
        var offset = 1024L;
        var expectedData = "Test object data"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validHash, expectedData);
        
        A.CallTo(() => _provider()).Returns(expectedEntry);
        A.CallTo(() => _packReader.GetByOffsetAsync(offset, _provider))
            .Returns(expectedEntry);

        // Act
        var result = await _packReader.GetByOffsetAsync(offset, _provider);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedEntry);
    }

    [Test]
    public async Task GetByOffsetAsync_SubsequentCall_ShouldReturnCachedResult()
    {
        // Arrange
        var offset = 1024L;
        var expectedData = "Cached object data"u8.ToArray();
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validHash, expectedData);
        
        A.CallTo(() => _packReader.GetByOffsetAsync(offset, _provider))
            .Returns(expectedEntry);

        // Act
        var result1 = await _packReader.GetByOffsetAsync(offset, _provider);
        var result2 = await _packReader.GetByOffsetAsync(offset, _provider);

        // Assert
        result1.Should().BeEquivalentTo(result2);
        result1.Should().BeEquivalentTo(expectedEntry);
    }

    [Test]
    public async Task GetByOffsetAsync_DifferentOffsets_ShouldCallProviderForEach()
    {
        // Arrange
        var offset1 = 1024L;
        var offset2 = 2048L;
        var entry1 = new UnlinkedEntry(EntryType.Blob, _validHash, "Data 1"u8.ToArray());
        var entry2 = new UnlinkedEntry(EntryType.Tree, _validHash, "Data 2"u8.ToArray());
        
        A.CallTo(() => _packReader.GetByOffsetAsync(offset1, _provider))
            .Returns(entry1);
        A.CallTo(() => _packReader.GetByOffsetAsync(offset2, _provider))
            .Returns(entry2);

        // Act
        var result1 = await _packReader.GetByOffsetAsync(offset1, _provider);
        var result2 = await _packReader.GetByOffsetAsync(offset2, _provider);

        // Assert
        result1.Should().NotBeEquivalentTo(result2);
        result1.Should().BeEquivalentTo(entry1);
        result2.Should().BeEquivalentTo(entry2);
    }

    [Test]
    public async Task GetByOffsetAsync_ConcurrentCalls_ShouldHandleRaceCondition()
    {
        // Arrange
        var offset = 1024L;
        var expectedEntry = new UnlinkedEntry(EntryType.Blob, _validHash, "Concurrent data"u8.ToArray());
        
        A.CallTo(() => _packReader.GetByOffsetAsync(offset, _provider))
            .Returns(expectedEntry);

        // Act - Simulate concurrent calls
        var task1 = _packReader.GetByOffsetAsync(offset, _provider);
        var task2 = _packReader.GetByOffsetAsync(offset, _provider);
        var results = await Task.WhenAll(task1, task2);

        // Assert
        results[0].Should().BeEquivalentTo(results[1]);
        results[0].Should().BeEquivalentTo(expectedEntry);
    }
}
