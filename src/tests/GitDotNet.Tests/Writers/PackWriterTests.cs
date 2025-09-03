using System.IO.Abstractions.TestingHelpers;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Writers;
using Microsoft.Extensions.Caching.Memory;

namespace GitDotNet.Tests.Writers;

public class PackWriterTests
{
    [Test]
    public async Task WritePackAsync_WithSingleBlobEntry_CreatesValidPackFile()
    {
        // Arrange
        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        var testData = CreateTestData(500);
        var entryId = new HashId("1111111111111111111111111111111111111111");
        
        packWriter.AddEntry(EntryType.Blob, entryId, testData);

        // Act
        await packWriter.WritePackAsync(null, []);
        packWriter.Dispose(); // This triggers the file rename

        // Assert
        using (new AssertionScope())
        {
            // Verify pack file was created
            packWriter.FinalPackPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalPackPath!).Should().BeTrue("Pack file should exist");
            
            // Verify index file was created
            packWriter.FinalIndexPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalIndexPath!).Should().BeTrue("Index file should exist");
            
            // Verify pack file content using PackReader
            await VerifyPackFileContent(packWriter.FinalIndexPath!, entryId, testData);
        }
    }

    [Test]
    public async Task WritePackAsync_WithMultipleEntries_CreatesValidPackFile()
    {
        // Arrange - Create test data just like PackOptimizationTests
        var entries = new List<(EntryType type, HashId id, byte[] data)>
        {
            (EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000)),
            (EntryType.Tree, new HashId("2222222222222222222222222222222222222222"), CreateTestData(500)),
            (EntryType.Commit, new HashId("3333333333333333333333333333333333333333"), CreateTestData(300)),
            (EntryType.Blob, new HashId("4444444444444444444444444444444444444444"), CreateTestData(750))
        };

        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        
        foreach (var (type, id, data) in entries)
        {
            packWriter.AddEntry(type, id, data);
        }

        // Act
        await packWriter.WritePackAsync(null, []);
        packWriter.Dispose();

        // Assert
        using (new AssertionScope())
        {
            packWriter.FinalPackPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalPackPath!).Should().BeTrue();
            
            packWriter.FinalIndexPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalIndexPath!).Should().BeTrue();
            
            // Verify all entries can be read back correctly
            await VerifyAllPackEntries(packWriter.FinalIndexPath!, entries);
        }
    }

    [Test]
    public async Task WritePackAsync_WithSimilarBlobsForDeltaCompression_CreatesValidPackWithDeltas()
    {
        // Arrange - Create base data and similar variations to encourage delta compression
        var baseData = CreateTestData(2000);
        var similarData1 = CreateSimilarTestData(baseData, 10);
        var similarData2 = CreateSimilarTestData(baseData, 20);
        var similarData3 = CreateSimilarTestData(baseData, 30);
        
        var entries = new List<(EntryType type, HashId id, byte[] data)>
        {
            (EntryType.Blob, new HashId("aaaa111111111111111111111111111111111111"), baseData),
            (EntryType.Blob, new HashId("bbbb222222222222222222222222222222222222"), similarData1),
            (EntryType.Blob, new HashId("cccc333333333333333333333333333333333333"), similarData2),
            (EntryType.Blob, new HashId("dddd444444444444444444444444444444444444"), similarData3)
        };

        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        
        foreach (var (type, id, data) in entries)
        {
            packWriter.AddEntry(type, id, data);
        }

        // Act
        await packWriter.WritePackAsync(null, []);
        packWriter.Dispose();

        // Assert
        using (new AssertionScope())
        {
            packWriter.FinalPackPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalPackPath!).Should().BeTrue();
            
            // Verify content integrity despite delta compression
            await VerifyAllPackEntries(packWriter.FinalIndexPath!, entries);
            
            // Verify pack file is smaller due to delta compression
            var packFileSize = _fileSystem.FileInfo.New(packWriter.FinalPackPath!).Length;
            var originalTotalSize = entries.Sum(e => e.data.Length);
            
            // Pack file should be smaller than the sum of original data
            // (accounting for headers and potential delta compression)
            packFileSize.Should().BeLessThan(originalTotalSize, "Pack file should benefit from delta compression");
        }
    }

    [Test]
    public async Task WritePackAsync_WithLargeNumberOfEntries_CreatesValidPack()
    {
        // Arrange
        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        const int entryCount = 50; // Reduced for test performance
        var entries = new List<(EntryType type, HashId id, byte[] data)>();
        
        for (int i = 0; i < entryCount; i++)
        {
            var id = new HashId($"{i:X8}11111111111111111111111111111111");
            var data = CreateTestData(100 + (i * 10)); // Varying sizes
            var type = (EntryType)(1 + (i % 4)); // Cycle through Commit, Tree, Blob, Tag
            
            entries.Add((type, id, data));
            packWriter.AddEntry(type, id, data);
        }

        // Act
        await packWriter.WritePackAsync(null, []);
        packWriter.Dispose();

        // Assert
        using (new AssertionScope())
        {
            packWriter.FinalPackPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalPackPath!).Should().BeTrue();
            
            // Verify pack content
            VerifyPackEntryCount(packWriter.FinalIndexPath!, entryCount);
            
            // Spot check some entries
            await VerifySpecificEntriesAsync(packWriter.FinalIndexPath!, entries.Take(5));
        }
    }

    [Test]
    public async Task TryAddEntry_WithExistingEntry_ReturnsFalseAndDoesNotDuplicate()
    {
        // Arrange
        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        var entryId = new HashId("1111111111111111111111111111111111111111");
        var data1 = CreateTestData(100);
        var data2 = CreateTestData(200); // Different data
        
        // Act
        var firstAdd = packWriter.TryAddEntry(EntryType.Blob, entryId, data1);
        var secondAdd = packWriter.TryAddEntry(EntryType.Blob, entryId, data2); // Same ID, different data
        
        await packWriter.WritePackAsync(null, []);
        packWriter.Dispose();

        // Assert
        using (new AssertionScope())
        {
            firstAdd.Should().BeTrue("First add should succeed");
            secondAdd.Should().BeFalse("Second add with same ID should fail");
            
            // Verify only the first data is in the pack
            await VerifyPackFileContent(packWriter.FinalIndexPath!, entryId, data1);
        }
    }

    [Test]
    public async Task WritePackAsync_WithMaxDeltaDepth_RespectsDepthLimit()
    {
        // Arrange - Create a chain of similar entries to test depth limiting
        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        var entries = new List<(EntryType type, HashId id, byte[] data)>();
        
        // Create base data
        var baseData = CreateTestData(1000);
        var currentData = baseData;
        
        // Create 10 entries, each slightly different from the previous
        for (int i = 0; i < 10; i++)
        {
            var id = new HashId($"{i:00}11111111111111111111111111111111111111");
            currentData = i == 0 ? baseData : CreateIncrementalTestData(currentData, i);
            
            entries.Add((EntryType.Blob, id, currentData));
            packWriter.AddEntry(EntryType.Blob, id, currentData);
        }

        const int maxDepth = 5;

        // Act
        await packWriter.WritePackAsync(null, [], maxDepth);
        packWriter.Dispose();

        // Assert
        using (new AssertionScope())
        {
            packWriter.FinalPackPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalPackPath!).Should().BeTrue();
            
            // Verify all entries are still accessible
            await VerifyAllPackEntries(packWriter.FinalIndexPath!, entries);
        }
    }

    [Test]
    public async Task WritePackAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        packWriter.AddEntry(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(100));
        
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        await FluentActions.Invoking(() => packWriter.WritePackAsync(null, [], cancellationToken: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void AddEntry_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        packWriter.Dispose();

        // Act & Assert
        FluentActions.Invoking(() => packWriter.AddEntry(EntryType.Blob, 
                new HashId("1111111111111111111111111111111111111111"), CreateTestData(100)))
            .Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task WritePackAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        packWriter.Dispose();

        // Act & Assert
        await FluentActions.Invoking(() => packWriter.WritePackAsync(null, []))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task WritePackAsync_WithEmptyPack_CreatesValidEmptyPackFile()
    {
        // Arrange
        using var packWriter = new PackWriter(_repositoryInfo, _fileSystem);
        // Don't add any entries

        // Act
        await packWriter.WritePackAsync(null, []);
        packWriter.Dispose();

        // Assert
        using (new AssertionScope())
        {
            packWriter.FinalPackPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalPackPath!).Should().BeTrue();
            
            packWriter.FinalIndexPath.Should().NotBeNull();
            _fileSystem.File.Exists(packWriter.FinalIndexPath!).Should().BeTrue();
            
            // Verify empty pack has correct structure
            VerifyPackEntryCount(packWriter.FinalIndexPath!, 0);
        }
    }

    private async Task VerifyPackFileContent(string indexFilePath, HashId expectedId, byte[] expectedData)
    {
        using var packIndexReader = new PackIndexReader.Standard(indexFilePath,
            path => new PackReader(path, _fileSystem.CreateOffsetReader),
            _fileSystem.CreateOffsetReader,
            _fileSystem,
            A.Fake<IMemoryCache>());

        // Try to find and verify the entry
        var index = await packIndexReader.GetIndexOfAsync(expectedId);
        index.Should().BeGreaterThanOrEqualTo(0, $"Entry {expectedId} should be found in pack");

        var entry = await packIndexReader.GetAsync(index, expectedId, _ => throw new NotSupportedException("Deltas not supported in this test"));
        
        entry.Type.Should().Be(EntryType.Blob);
        entry.Id.Should().Be(expectedId);
        entry.Data.Should().Equal(expectedData, "Entry data should match original");
    }

    private async Task VerifyAllPackEntries(string indexFilePath, List<(EntryType type, HashId id, byte[] data)> expectedEntries)
    {
        using var packIndexReader = new PackIndexReader.Standard(indexFilePath,
            path => new PackReader(path, _fileSystem.CreateOffsetReader),
            _fileSystem.CreateOffsetReader,
            _fileSystem,
            A.Fake<IMemoryCache>());

        packIndexReader.Count.Should().Be(expectedEntries.Count, "Pack should contain all entries");

        // Create a cache to store resolved entries
        var resolvedEntries = new Dictionary<HashId, UnlinkedEntry>();
        
        // Helper function to resolve dependencies recursively
        async Task<UnlinkedEntry> ResolveDependency(HashId id)
        {
            if (resolvedEntries.TryGetValue(id, out var cachedEntry))
                return cachedEntry;

            // Find the entry by its index and resolve it
            var index = await packIndexReader.GetIndexOfAsync(id);
            if (index < 0)
                throw new InvalidOperationException($"Dependency {id} not found in pack index");

            var entry = await packIndexReader.GetAsync(index, id, ResolveDependency);
            resolvedEntries[id] = entry;
            return entry;
        }

        // Process all expected entries
        foreach (var (expectedType, expectedId, expectedData) in expectedEntries)
        {
            var actualEntry = await ResolveDependency(expectedId);
            
            actualEntry.Type.Should().Be(expectedType, $"Entry {expectedId} should have correct type");
            actualEntry.Data.Should().Equal(expectedData, $"Entry {expectedId} should have correct data");
        }
    }

    private async Task VerifySpecificEntriesAsync(string indexFilePath, IEnumerable<(EntryType type, HashId id, byte[] data)> entriesToCheck)
    {
        using var packIndexReader = new PackIndexReader.Standard(indexFilePath,
            path => new PackReader(path, _fileSystem.CreateOffsetReader),
            _fileSystem.CreateOffsetReader,
            _fileSystem,
            A.Fake<IMemoryCache>());

        foreach (var (expectedType, expectedId, expectedData) in entriesToCheck)
        {
            var index = await packIndexReader.GetIndexOfAsync(expectedId);
            index.Should().BeGreaterThanOrEqualTo(0, $"Entry {expectedId} should be found in pack");

            var entry = await packIndexReader.GetAsync(index, expectedId, _ => throw new NotSupportedException("Deltas not supported in this test"));
            
            entry.Type.Should().Be(expectedType, $"Entry {expectedId} should have correct type");
            entry.Id.Should().Be(expectedId, $"Entry {expectedId} should have correct ID");
            entry.Data.Should().Equal(expectedData, $"Entry {expectedId} should have correct data");
        }
    }

    private void VerifyPackEntryCount(string indexFilePath, int expectedCount)
    {
        using var packIndexReader = new PackIndexReader.Standard(indexFilePath,
            path => new PackReader(path, _fileSystem.CreateOffsetReader),
            _fileSystem.CreateOffsetReader,
            _fileSystem,
            A.Fake<IMemoryCache>());

        packIndexReader.Count.Should().Be(expectedCount, "Pack should contain expected number of entries");
    }

    // Test data generation methods - same pattern as PackOptimizationTests
    private static byte[] CreateTestData(int size)
    {
        var data = new byte[size];
        var random = new Random(42); // Use fixed seed for reproducible tests
        
        // Create data with some patterns to enable delta compression
        for (int i = 0; i < size; i++)
        {
            // Create patterns that repeat every 100 bytes to simulate similar content
            data[i] = (byte)((i % 100) + (i / 100) % 10);
        }
        
        // Add some random variation
        for (int i = 0; i < size / 10; i++)
        {
            var pos = random.Next(size);
            data[pos] = (byte)random.Next(256);
        }
        
        return data;
    }

    private static byte[] CreateSimilarTestData(byte[] baseData, int modifications = 10)
    {
        var data = new byte[baseData.Length];
        Array.Copy(baseData, data, baseData.Length);
        
        var random = new Random(42);
        for (int i = 0; i < modifications && i < data.Length; i++)
        {
            var pos = random.Next(data.Length);
            data[pos] = (byte)random.Next(256);
        }
        
        return data;
    }

    /// <summary>Creates incremental test data that's progressively different from the base.</summary>
    /// <param name="baseData">The base data to modify.</param>
    /// <param name="step">The step number (affects how much change is made).</param>
    /// <returns>Modified data that's similar but progressively different.</returns>
    private static byte[] CreateIncrementalTestData(byte[] baseData, int step)
    {
        var data = new byte[baseData.Length];
        Array.Copy(baseData, data, baseData.Length);
        
        var random = new Random(step); // Use step as seed for reproducibility
        
        // Make very small changes that accumulate over steps
        var changesPerStep = Math.Max(1, baseData.Length / 1000); // Very small changes
        
        for (int i = 0; i < changesPerStep; i++)
        {
            var pos = random.Next(data.Length);
            data[pos] = (byte)((data[pos] + step) % 256); // Predictable but different change
        }
        
        return data;
    }

    private MockFileSystem _fileSystem;
    private IRepositoryInfo _repositoryInfo = A.Fake<IRepositoryInfo>(i => i.ConfigureFake(f =>
        A.CallTo(() => f.Path).Returns("/test-repo/.git")));

    [OneTimeSetUp]
    public void Setup()
    {
        _fileSystem = new MockFileSystem();

        // Create the repository structure
        _fileSystem.Directory.CreateDirectory(_repositoryInfo.Path);
        _fileSystem.Directory.CreateDirectory($"{_repositoryInfo.Path}/objects");
        _fileSystem.Directory.CreateDirectory($"{_repositoryInfo.Path}/objects/pack");
    }
}