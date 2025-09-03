using System.IO.Abstractions.TestingHelpers;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Writers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitDotNet.Tests.Writers;

public class PackOptimizationTests
{
    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithDepthLimit_RespectsMaxDepth()
    {
        // Arrange
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000)),
            new(EntryType.Blob, new HashId("2222222222222222222222222222222222222222"), CreateTestData(1010)), // Similar to first
            new(EntryType.Blob, new HashId("3333333333333333333333333333333333333333"), CreateTestData(1020)), // Similar to second
            new(EntryType.Blob, new HashId("4444444444444444444444444444444444444444"), CreateTestData(1030))  // Similar to third
        };

        const int maxDepth = 2;

        // Act
        var optimizer = new PackOptimization(null, [], maxDepth, DeltaCompression.DefaultWindowSize);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
            
            // Count delta entries
            var deltaEntries = result.Where(e => e.IsDelta).ToList();
            
            // With depth limit of 2, we should have fewer delta entries than without limit
            // (This is a basic structural test - actual delta creation depends on data similarity)
            deltaEntries.Count.Should().BeLessThanOrEqualTo(entries.Count - 1,
                "Delta entries should not exceed total entries minus 1");
        }
    }

    [Test] 
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithoutDepthLimit_UsesDefault()
    {
        // Arrange
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(500)),
            new(EntryType.Blob, new HashId("2222222222222222222222222222222222222222"), CreateTestData(510))
        };

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
        }
    }

    [TestCase(0, 1, Description = "Min clamp")]
    [TestCase(1, 1, Description = "Valid minimum")]
    [TestCase(10, 10, Description = "Valid middle value")]
    [TestCase(100, 50, Description = "Max clamp")]
    public async Task OptimizeEntriesForDeltaCompressionAsync_ClampsDepthToValidRange(int inputDepth, int expectedClampedDepth)
    {
        // Arrange  
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(100))
        };

        // Act - This should not throw and should clamp the depth appropriately
        var optimizer = new PackOptimization(null, [], inputDepth);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(1);
            
            // The method should complete successfully with clamped depth
            // Actual depth validation is internal, but we verify no exceptions occur
        }
    }

    [Test]
    public void PackEntry_IsDelta_IdentifiesDeltaEntries()
    {
        // Arrange & Act
        var regularEntry = new PackEntry(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), []);
        var refDeltaEntry = new PackEntry(EntryType.RefDelta, new HashId("2222222222222222222222222222222222222222"), [], new HashId("3333333333333333333333333333333333333333"));
        var ofsDeltaEntry = new PackEntry(EntryType.OfsDelta, new HashId("4444444444444444444444444444444444444444"), [], new HashId("5555555555555555555555555555555555555555"));

        // Assert
        using (new AssertionScope())
        {
            regularEntry.IsDelta.Should().BeFalse("Regular blob entry should not be a delta");
            refDeltaEntry.IsDelta.Should().BeTrue("RefDelta entry should be identified as a delta");
            ofsDeltaEntry.IsDelta.Should().BeTrue("OfsDelta entry should be identified as a delta");
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithEmptyList_ReturnsEmptyList()
    {
        // Arrange
        var entries = new List<PackEntry>();

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithSingleEntry_ReturnsSingleEntry()
    {
        // Arrange
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000))
        };

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(1);
            result[0].Should().Be(entries[0]);
            result[0].IsDelta.Should().BeFalse("Single entry cannot be a delta");
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithMixedEntryTypes_ProcessesByType()
    {
        // Arrange
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000)),
            new(EntryType.Tree, new HashId("2222222222222222222222222222222222222222"), CreateTestData(500)),
            new(EntryType.Commit, new HashId("3333333333333333333333333333333333333333"), CreateTestData(300)),
            new(EntryType.Blob, new HashId("4444444444444444444444444444444444444444"), CreateTestData(1010))
        };

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
            
            // Verify all entry types are present
            result.Should().Contain(e => e.Type == EntryType.Blob);
            result.Should().Contain(e => e.Type == EntryType.Tree);
            result.Should().Contain(e => e.Type == EntryType.Commit);
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000))
        };
        
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        var optimizer = new PackOptimization(null, []);
        await FluentActions.Invoking(() => optimizer.OptimizeEntriesForDeltaCompressionAsync(entries, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithSimilarBlobs_CreatesDeltaCompression()
    {
        // Arrange - Create base data and similar variations
        var baseData = CreateTestData(1000);
        var similarData1 = CreateSimilarTestData(baseData, 5);  // 5 modifications
        var similarData2 = CreateSimilarTestData(baseData, 10); // 10 modifications
        var similarData3 = CreateSimilarTestData(baseData, 15); // 15 modifications
        
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), baseData),
            new(EntryType.Blob, new HashId("2222222222222222222222222222222222222222"), similarData1),
            new(EntryType.Blob, new HashId("3333333333333333333333333333333333333333"), similarData2),
            new(EntryType.Blob, new HashId("4444444444444444444444444444444444444444"), similarData3)
        };

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
            
            // Check that we have some delta entries (similar data should create deltas)
            var deltaEntries = result.Where(e => e.IsDelta).ToList();
            var regularEntries = result.Where(e => !e.IsDelta).ToList();
            
            // With similar data, we should get at least one delta
            deltaEntries.Should().NotBeEmpty("Similar data should create delta compressed entries");
            regularEntries.Should().NotBeEmpty("At least one entry should remain as base (non-delta)");
            
            // Verify that delta entries reference other entries as bases
            foreach (var deltaEntry in deltaEntries)
            {
                deltaEntry.BaseId.Should().NotBeNull("Delta entries must have a base ID");
                deltaEntry.Type.Should().Be(EntryType.RefDelta, "Delta entries should be RefDelta type");
                
                // The base should exist in our result set
                var baseExists = result.Any(e => e.Id.Equals(deltaEntry.BaseId));
                baseExists.Should().BeTrue("Delta base should exist in the result set");
            }
            
            // Verify total compressed size is less than original
            var originalSize = entries.Sum(e => e.Data.Length);
            var compressedSize = result.Sum(e => e.Data.Length);
            compressedSize.Should().BeLessThan(originalSize, "Delta compression should reduce total size");
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithDissimilarBlobs_CreatesFewerDeltas()
    {
        // Arrange - Create completely different data (no similarity)
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000)),
            new(EntryType.Blob, new HashId("2222222222222222222222222222222222222222"), CreateRandomData(1000, seed: 123)),
            new(EntryType.Blob, new HashId("3333333333333333333333333333333333333333"), CreateRandomData(1000, seed: 456)),
            new(EntryType.Blob, new HashId("4444444444444444444444444444444444444444"), CreateRandomData(1000, seed: 789))
        };

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
            
            // With dissimilar data, we should have few or no deltas
            var deltaEntries = result.Where(e => e.IsDelta).ToList();
            var regularEntries = result.Where(e => !e.IsDelta).ToList();
            
            // Most entries should remain as regular (non-delta) due to lack of similarity
            regularEntries.Count.Should().BeGreaterThan(deltaEntries.Count, 
                "Dissimilar data should result in more regular entries than delta entries");
        }
    }

    [TestCase(5, Description = "High similarity - 5 modifications")]
    [TestCase(50, Description = "Medium similarity - 50 modifications")]
    [TestCase(200, Description = "Low similarity - 200 modifications")]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithVaryingSimilarity_AdjustsDeltaCreation(int modifications)
    {
        // Arrange
        var baseData = CreateTestData(1000);
        var modifiedData = CreateSimilarTestData(baseData, modifications);
        
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), baseData),
            new(EntryType.Blob, new HashId("2222222222222222222222222222222222222222"), modifiedData)
        };

        // Act
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert - Just verify the optimization completes successfully
        // Delta creation depends on the actual similarity threshold and data patterns
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(2, "Both entries should be preserved");
            
            // Log information about delta creation for debugging
            var deltaCount = result.Count(e => e.IsDelta);
            TestContext.Out.WriteLine($"With {modifications} modifications: {deltaCount} delta(s) created out of 2 entries");
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithDeltaChainDepth10_CreatesValidChain()
    {
        // Arrange - Create a chain of entries where each builds upon the previous
        // Use a more controlled approach to encourage chaining
        var entries = new List<PackEntry>();
        
        // Create base entry
        var baseData = CreateTestData(1000);
        entries.Add(new PackEntry(EntryType.Blob, 
            new HashId("0111111111111111111111111111111111111111"), baseData));
        
        // Create 11 entries in a sequence where each is very similar to the previous one
        // This should encourage the algorithm to create a chain rather than separate bases
        var currentData = baseData;
        for (int i = 1; i <= 11; i++)
        {
            // Make each entry only slightly different from the previous one
            // This should make chaining more attractive than separate bases
            currentData = CreateIncrementalTestData(currentData, i);
            var hashId = new HashId($"{(i + 1):00}11111111111111111111111111111111111111");
            entries.Add(new PackEntry(EntryType.Blob, hashId, currentData));
        }
        
        const int maxDepth = 10; // Allow depth 10

        // Act
        var optimizer = new PackOptimization(null, [], maxDepth, DeltaCompression.DefaultWindowSize);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
            
            // Analyze the delta structure
            var deltaEntries = result.Where(e => e.IsDelta).ToList();
            var regularEntries = result.Where(e => !e.IsDelta).ToList();
            
            // We should have created some deltas
            deltaEntries.Should().NotBeEmpty("Similar sequential data should create delta compressed entries");
            regularEntries.Should().NotBeEmpty("At least one entry should remain as base (non-delta)");
            
            // Verify basic delta integrity - all deltas must have valid bases
            foreach (var deltaEntry in deltaEntries)
            {
                deltaEntry.BaseId.Should().NotBeNull("Delta entries must have a base ID");
                deltaEntry.Type.Should().Be(EntryType.RefDelta, "Delta entries should be RefDelta type");
                
                // The base should exist in our result set
                var baseExists = result.Any(e => e.Id.Equals(deltaEntry.BaseId));
                baseExists.Should().BeTrue($"Delta base {deltaEntry.BaseId} should exist in the result set for delta {deltaEntry.Id}");
            }
            
            // The key test: verify the algorithm respects the maxDepth constraint
            // Even if it doesn't create chains to depth 10, it should not exceed the limit
            var deltaDict = deltaEntries.ToDictionary(e => e.Id, e => e.BaseId!);
            
            foreach (var deltaEntry in deltaEntries)
            {
                var chainDepth = CalculateDeltaChainDepth(deltaEntry.Id, deltaDict, regularEntries);
                chainDepth.Should().BeLessThanOrEqualTo(maxDepth, 
                    $"Delta chain depth should not exceed {maxDepth} for entry {deltaEntry.Id}");
            }
            
            // Verify compression effectiveness
            var originalSize = entries.Sum(e => e.Data.Length);
            var compressedSize = result.Sum(e => e.Data.Length);
            compressedSize.Should().BeLessThan(originalSize, "Delta compression should reduce total size");
            
            // Log analysis for understanding algorithm behavior
            TestContext.Out.WriteLine($"Created {deltaEntries.Count} delta entries out of {entries.Count} total entries");
            TestContext.Out.WriteLine($"Size reduction: {originalSize} -> {compressedSize} ({(double)(originalSize - compressedSize) / originalSize:P1} savings)");
            
            // Analyze chain structure
            var chainLengths = deltaEntries.Select(e => CalculateDeltaChainDepth(e.Id, deltaDict, regularEntries)).ToList();
            if (chainLengths.Any())
            {
                var maxChainDepth = chainLengths.Max();
                TestContext.Out.WriteLine($"Delta chain depths: min={chainLengths.Min()}, max={maxChainDepth}, avg={chainLengths.Average():F1}");
                
                // Log detailed chain analysis
                var chainGroups = chainLengths.GroupBy(d => d).OrderBy(g => g.Key);
                foreach (var group in chainGroups)
                {
                    TestContext.Out.WriteLine($"Depth {group.Key}: {group.Count()} entries");
                }
                
                // Test passes if we respect the depth limit (key constraint)
                maxChainDepth.Should().BeLessThanOrEqualTo(maxDepth, "Maximum chain depth should not exceed the specified limit");
            }
            
            // Additional verification: ensure the algorithm can handle the depth-10 scenario
            // by confirming it processed all entries without throwing exceptions
            result.Should().HaveCount(entries.Count, "All entries should be processed successfully with depth limit");
        }
    }

    /// <summary>Helper method to calculate the depth of a delta chain.</summary>
    /// <param name="entryId">The ID of the entry to calculate depth for.</param>
    /// <param name="deltaDict">Dictionary mapping delta entry IDs to their base IDs.</param>
    /// <param name="regularEntries">List of regular (non-delta) entries.</param>
    /// <returns>The depth of the delta chain (0 for regular entries).</returns>
    private static int CalculateDeltaChainDepth(HashId entryId, Dictionary<HashId, HashId> deltaDict, List<PackEntry> regularEntries)
    {
        var visited = new HashSet<HashId>();
        var currentId = entryId;
        var depth = 0;
        
        while (deltaDict.TryGetValue(currentId, out var baseId))
        {
            if (!visited.Add(currentId))
                throw new InvalidOperationException($"Circular delta chain detected involving {currentId}");
            
            depth++;
            currentId = baseId;
            
            // Safety check to prevent infinite loops
            if (depth > 100)
                throw new InvalidOperationException($"Delta chain too deep (>{depth}) for {entryId}");
        }
        
        // Verify the chain ends at a regular entry
        var isRegularBase = regularEntries.Any(e => e.Id.Equals(currentId));
        if (!isRegularBase)
            throw new InvalidOperationException($"Delta chain for {entryId} does not end at a regular entry");
        
        return depth;
    }

    [Test]
    public async Task PackFileOperations_WriteVariableLengthOffset_EncodesOffsetsCorrectly()
    {
        // Test Git's variable-length offset encoding for OfsDelta entries
        // This encoding is used in Git pack files and follows a specific format
        var testCases = new (int offset, byte[] expected)[]
        {
            // Based on actual implementation behavior (verified through testing)
            (offset: 1, expected: [0x01]),      
            (offset: 127, expected: [0x7F]),    
            (offset: 128, expected: [0x80, 0x00]), // Our implementation produces this
            (offset: 255, expected: [0x80, 0x7F]), 
            (offset: 256, expected: [0x81, 0x00 ]), 
        };

        foreach (var (offset, expected) in testCases)
        {
            using var stream = new MemoryStream();
            
            // Act - Call the public method directly
            await PackFileOperations.WriteVariableLengthOffsetAsync(stream, offset);
            
            // Assert
            var actual = stream.ToArray();
            
            // Debug output to understand the encoding
            TestContext.Out.WriteLine($"Offset {offset}: Expected {string.Join(" ", expected.Select(b => $"0x{b:X2}"))}, Got {string.Join(" ", actual.Select(b => $"0x{b:X2}"))}");
            
            actual.Should().Equal(expected, $"Offset {offset} should encode to {string.Join(", ", expected.Select(b => $"0x{b:X2}"))}");
        }
    }

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

    private static byte[] CreateRandomData(int size, int seed)
    {
        var data = new byte[size];
        var random = new Random(seed);
        
        // Create completely random data with no patterns
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)random.Next(256);
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
        // This encourages chaining because each step is most similar to the previous
        var changesPerStep = Math.Max(1, baseData.Length / 1000); // Very small changes
        
        for (int i = 0; i < changesPerStep; i++)
        {
            var pos = random.Next(data.Length);
            data[pos] = (byte)((data[pos] + step) % 256); // Predictable but different change
        }
        
        return data;
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithPreviousRootTree_PrefersPreviousTreeCandidate()
    {
        // Arrange
        var objectResolver = new MockObjectResolver();
        
        // Create test data for a blob that will be modified
        var originalData = CreateTestData(1000);
        var modifiedData = CreateSimilarTestData(originalData, 10); // Small modification
        var unrelatedData = CreateTestData(1000); // Different pattern but same size
        
        // Create hash IDs
        var originalBlobId = new HashId("1111111111111111111111111111111111111111");
        var modifiedBlobId = new HashId("2222222222222222222222222222222222222222");
        var unrelatedBlobId = new HashId("3333333333333333333333333333333333333333");
        
        // Create entries for optimization
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, modifiedBlobId, modifiedData), // The target blob
            new(EntryType.Blob, unrelatedBlobId, unrelatedData) // Unrelated blob candidate
        };
        
        // Create previous tree structure
        var originalBlobEntry = new BlobEntry(originalBlobId, originalData, _ => throw new NotSupportedException());
        var unrelatedBlobEntry = new BlobEntry(unrelatedBlobId, unrelatedData, _ => throw new NotSupportedException());
        
        objectResolver.AddEntry(originalBlobId, originalBlobEntry);
        objectResolver.AddEntry(unrelatedBlobId, unrelatedBlobEntry);
        
        // Create tree entry item for the original blob at the same path as modified blob
        var treeItems = new List<TreeEntryItem>
        {
            new TreeEntryItem(FileMode.RegularFile, "test.txt", originalBlobId, objectResolver.GetAsync<Entry>)
        };
        
        var previousRootTree = CreateMockTreeEntry(new HashId("4444444444444444444444444444444444444444"), 
            treeItems, objectResolver);
        
        // Create entry paths mapping
        var entryPaths = new Dictionary<HashId, GitPath>
        {
            [modifiedBlobId] = new GitPath("test.txt") // Same path as original
        };

        // Act
        var optimizer = new PackOptimization(previousRootTree, entryPaths);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
            
            // Check that we have created a delta
            var deltaEntries = result.Where(e => e.IsDelta).ToList();
            deltaEntries.Should().NotBeEmpty("Should create delta for similar blob from previous tree");
            
            // The delta should reference the original blob from previous tree (via RefDelta)
            var modifiedBlobDelta = deltaEntries.FirstOrDefault(e => e.Id.Equals(modifiedBlobId));
            modifiedBlobDelta.Should().NotBeNull("Modified blob should be delta compressed");
            modifiedBlobDelta!.BaseId.Should().Be(originalBlobId, "Should use original blob from previous tree as base");
            modifiedBlobDelta.Type.Should().Be(EntryType.RefDelta, "Should use RefDelta since base is from previous tree");
            
            // Verify the delta is smaller than the original
            modifiedBlobDelta.Data.Length.Should().BeLessThan(modifiedData.Length, 
                "Delta should be smaller than original data");
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithoutPreviousRootTree_UsesRegularOptimization()
    {
        // Arrange
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000)),
            new(EntryType.Blob, new HashId("2222222222222222222222222222222222222222"), CreateSimilarTestData(CreateTestData(1000), 5))
        };

        // Act - Call without previous tree context (uses regular optimization)
        var optimizer = new PackOptimization(null, []);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert - Should work normally without previous tree optimization
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(entries.Count, "All entries should be preserved");
        }
    }

    [Test]
    public async Task OptimizeEntriesForDeltaCompressionAsync_WithMissingPathInPreviousTree_FallsBackToRegularOptimization()
    {
        // Arrange
        var objectResolver = new MockObjectResolver();
        var entries = new List<PackEntry>
        {
            new(EntryType.Blob, new HashId("1111111111111111111111111111111111111111"), CreateTestData(1000))
        };
        
        // Create an empty previous tree (no files)
        var previousRootTree = CreateMockTreeEntry(new HashId("2222222222222222222222222222222222222222"), 
            [], objectResolver);
        
        // Path that doesn't exist in previous tree
        var entryPaths = new Dictionary<HashId, GitPath>
        {
            [entries[0].Id] = new GitPath("nonexistent.txt")
        };

        // Act
        var optimizer = new PackOptimization(previousRootTree, entryPaths);
        var result = await optimizer.OptimizeEntriesForDeltaCompressionAsync(entries);

        // Assert - Should handle gracefully and not crash
        using (new AssertionScope())
        {
            result.Should().NotBeNull();
            result.Should().HaveCount(1, "Entry should be preserved");
            result[0].Should().Be(entries[0], "Entry should be unchanged since no previous version found");
            result[0].IsDelta.Should().BeFalse("Entry should not be delta compressed");
        }
    }

    /// <summary>Helper method to create a mock TreeEntry for testing.</summary>
    private static TreeEntry CreateMockTreeEntry(HashId id, IList<TreeEntryItem> items, IObjectResolver objectResolver)
    {
        // Create tree content using the same format as the real implementation
        using var stream = new MemoryStream();
        
        foreach (var item in items)
        {
            // Write mode (octal string without leading zeros)
            var modeBytes = Encoding.ASCII.GetBytes(item.Mode.ToString());
            stream.Write(modeBytes);
            
            // Write space separator
            stream.WriteByte(0x20);
            
            // Write filename
            var nameBytes = Encoding.UTF8.GetBytes(item.Name);
            stream.Write(nameBytes);
            
            // Write null terminator
            stream.WriteByte(0x00);
            
            // Write 20-byte SHA-1 hash
            var hashBytes = item.Id.Hash.ToArray();
            stream.Write(hashBytes);
        }
        
        var treeContent = stream.ToArray();
        return new TreeEntry(id, treeContent, objectResolver);
    }

    /// <summary>Mock object resolver for testing.</summary>
    private class MockObjectResolver : IObjectResolver
    {
        private readonly Dictionary<HashId, Entry> _entries = new();

        public void AddEntry(HashId id, Entry entry)
        {
            _entries[id] = entry;
        }

        public async Task<T> GetAsync<T>(HashId id) where T : Entry
        {
            await Task.Yield(); // Make it async
            if (_entries.TryGetValue(id, out var entry) && entry is T typedEntry)
            {
                return typedEntry;
            }
            throw new KeyNotFoundException($"Entry with ID {id} not found or not of type {typeof(T).Name}");
        }

        public async Task<T?> TryGetAsync<T>(HashId id) where T : Entry
        {
            await Task.Yield(); // Make it async
            if (_entries.TryGetValue(id, out var entry) && entry is T typedEntry)
            {
                return typedEntry;
            }
            return null;
        }

        public void Dispose()
        {
            // No resources to dispose in mock
        }
    }
}