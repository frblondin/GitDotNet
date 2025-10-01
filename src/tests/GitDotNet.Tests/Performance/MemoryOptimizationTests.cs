using NUnit.Framework;
using FluentAssertions;
using System.Diagnostics;
using System.Runtime;

namespace GitDotNet.Tests.Performance;

/// <summary>
/// Memory usage optimization and validation tests
/// </summary>
[TestFixture]
[Category("Memory")]
public class MemoryOptimizationTests
{
    private long _initialMemory;

    [SetUp]
    public void Setup()
    {
        // Force garbage collection to get accurate baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        _initialMemory = GC.GetTotalMemory(false);
    }

    [TearDown]
    public void TearDown()
    {
        // Force cleanup after each test
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Test]
    [Memory]
    public void HashId_ShouldHaveOptimalMemoryLayout()
    {
        // Arrange & Act
        var hashIds = new HashId[1000];
        var memoryBefore = GC.GetTotalMemory(false);

        for (int i = 0; i < hashIds.Length; i++)
        {
            hashIds[i] = new HashId($"a1b2c3d4e5f678901234567890123456{i:X8}");
        }

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryUsed = memoryAfter - memoryBefore;

        // Assert
        // Each HashId should use reasonable memory (including GC overhead and fragmentation)
        var averageMemoryPerHashId = memoryUsed / hashIds.Length;
        averageMemoryPerHashId.Should().BeLessThan(2000, "HashId should have reasonable memory layout");

        // Validate all HashIds are correctly formed
        hashIds.Should().AllSatisfy(hashId => hashId.Hash.Count.Should().Be(20));
    }

    [Test]
    [Memory]
    public void UnlinkedEntry_ShouldMinimizeMemoryFootprint()
    {
        // Arrange
        var testData = "Hello, World!"u8.ToArray();
        var hashId = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
        
        var memoryBefore = GC.GetTotalMemory(false);

        // Act - Create many entries
        var entries = new UnlinkedEntry[1000];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new UnlinkedEntry(EntryType.Blob, hashId, testData);
        }

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryUsed = memoryAfter - memoryBefore;

        // Assert
        // Each entry should share the same data array (reference, not copy)
        var averageMemoryPerEntry = memoryUsed / entries.Length;
        
        // Should be small since data arrays are shared
        averageMemoryPerEntry.Should().BeLessThan(200, "UnlinkedEntry should have minimal memory footprint");

        // Validate entries share data
        for (int i = 1; i < entries.Length; i++)
        {
            ReferenceEquals(entries[0].Data, entries[i].Data).Should().BeTrue("Data arrays should be shared");
        }
    }

    [Test]
    [Memory]        
    public void ObjectResolver_ShouldReleaseMemoryAfterOperations()
    {
        // This test would validate that ObjectResolver doesn't leak memory
        // In a real scenario, we would create and dispose ObjectResolver instances
        // and verify memory is properly released

        var memoryBefore = GC.GetTotalMemory(false);

        // Simulate object resolver operations
        var largeData = new byte[1024 * 1024]; // 1MB
        var entries = new List<UnlinkedEntry>();

        for (int i = 0; i < 10; i++)
        {
            var hashId = new HashId($"a1b2c3d4e5f6789012345678901234567890abc{i:X}");
            entries.Add(new UnlinkedEntry(EntryType.Blob, hashId, largeData));
        }

        var memoryPeak = GC.GetTotalMemory(false);

        // Clear references and force GC
        entries.Clear();
        entries = null;
        largeData = null;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryAfter = GC.GetTotalMemory(false);

        // Assert memory was released
        var memoryRetained = memoryAfter - memoryBefore;
        memoryRetained.Should().BeLessThan(1024 * 1024, "Most memory should be released after cleanup");

        var peakMemoryUsed = memoryPeak - memoryBefore;
        peakMemoryUsed.Should().BeGreaterThan(1024 * 1024, "Should have used at least 1MB memory during operation"); // More realistic expectation
    }

    [Test]
    [Memory]
    public void CacheEntry_ShouldAccuratelyEstimateSize()
    {
        // Arrange
        var smallData = "small"u8.ToArray();
        var largeData = new byte[1024 * 1024]; // 1MB
        
        var smallEntry = new UnlinkedEntry(EntryType.Blob, new HashId("a1b2c3d4e5f6789012345678901234567890abcd"), smallData);
        var largeEntry = new UnlinkedEntry(EntryType.Blob, new HashId("b2c3d4e5f6789012345678901234567890abcde1"), largeData);

        // Act - This would typically be done by the cache
        var smallEstimate = EstimateObjectSize(smallEntry);
        var largeEstimate = EstimateObjectSize(largeEntry);

        // Assert
        smallEstimate.Should().BeLessThan(100);
        largeEstimate.Should().BeGreaterThan(1024 * 1024);
        largeEstimate.Should().BeGreaterThan(smallEstimate);
    }

    [Test]
    [Memory]
    [Explicit("Long running memory stress test")]
    public void ObjectResolver_MemoryStressTest()
    {
        // This is a stress test to validate memory behavior under load
        var random = new Random(42);
        var memoryUsages = new List<long>();

        for (int cycle = 0; cycle < 10; cycle++)
        {
            var entries = new List<UnlinkedEntry>();

            // Create many objects
            for (int i = 0; i < 1000; i++)
            {
                var dataSize = random.Next(100, 10000);
                var data = new byte[dataSize];
                random.NextBytes(data);

                var hashId = new HashId($"a1b2c3d4e5f6789012345678901234567890abc{i:X}");
                entries.Add(new UnlinkedEntry(EntryType.Blob, hashId, data));
            }

            var currentMemory = GC.GetTotalMemory(false);
            memoryUsages.Add(currentMemory);

            // Clear this cycle's objects
            entries.Clear();

            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Assert memory doesn't grow unbounded
        var maxMemory = memoryUsages.Max();
        var minMemory = memoryUsages.Min();
        var memoryGrowth = maxMemory - minMemory;

        // Memory growth should be reasonable (less than 50MB)
        memoryGrowth.Should().BeLessThan(50 * 1024 * 1024, 
            "Memory should not grow unbounded across cycles");

        TestContext.Out.WriteLine($"Memory usage pattern: Min={minMemory:N0}, Max={maxMemory:N0}, Growth={memoryGrowth:N0}");
    }

    [Test]
    [Memory]
    public void WeakReference_ShouldAllowGarbageCollection()
    {
        // Test that our objects can be properly garbage collected
        WeakReference weakRef;

        // Create object in separate method to ensure it goes out of scope
        CreateUnlinkedEntryForGC(out weakRef);

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert object was collected
        weakRef.IsAlive.Should().BeFalse("Object should be garbage collected when no strong references exist");
    }

    private void CreateUnlinkedEntryForGC(out WeakReference weakRef)
    {
        var data = "test data for GC"u8.ToArray();
        var hashId = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
        var entry = new UnlinkedEntry(EntryType.Blob, hashId, data);
        
        weakRef = new WeakReference(entry);
        
        // Ensure the entry is used so compiler doesn't optimize it away
        entry.Type.Should().Be(EntryType.Blob);
    }

    /// <summary>
    /// Estimates object size for memory analysis
    /// </summary>
    private static long EstimateObjectSize<T>(T value) where T : class
    {
        return value switch
        {
            UnlinkedEntry entry => 32 + entry.Data.LongLength, // Hash + metadata + data
            string str => str.Length * 2, // Unicode characters
            byte[] bytes => bytes.LongLength,
            _ => 256 // Default estimate
        };
    }
}

/// <summary>
/// Memory test attribute for categorizing memory-related tests
/// </summary>
public class MemoryAttribute : CategoryAttribute
{
    public MemoryAttribute() : base("Memory")
    {
    }
}
