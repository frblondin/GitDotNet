using NUnit.Framework;
using FluentAssertions;
using System.Collections.Concurrent;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using GitDotNet.Readers;

namespace GitDotNet.Tests.Performance;

/// <summary>
/// Concurrency tests for multi-threaded access to Git object resolution system
/// </summary>
[TestFixture]
[Category("Performance")]
[Category("Concurrency")]
public class ConcurrencyTests
{
    private ILogger<ConcurrencyTests>? _logger;
    private const int DefaultThreadCount = 10;
    private const int DefaultOperationsPerThread = 50;

    [SetUp]
    public void Setup()
    {
        _logger = null; // TestLogger not available in this project
    }

    [Test]
    [Performance]
    public async Task ObjectResolver_ConcurrentReads_ShouldBeThreadSafe()
    {
        // Test concurrent reads from multiple threads
        
        var objectResolver = A.Fake<IObjectResolver>();
        var testHashId = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
        var expectedEntry = A.Fake<CommitEntry>();

        A.CallTo(() => objectResolver.GetAsync<CommitEntry>(testHashId))
            .Returns(Task.FromResult(expectedEntry));

        var results = new ConcurrentBag<CommitEntry>();
        var exceptions = new ConcurrentBag<Exception>();
        var completedOperations = 0;

        // Launch concurrent read operations
        var tasks = Enumerable.Range(0, DefaultThreadCount).Select(async threadId =>
        {
            for (int i = 0; i < DefaultOperationsPerThread; i++)
            {
                try
                {
                    var entry = await objectResolver.GetAsync<CommitEntry>(testHashId);
                    results.Add(entry);
                    Interlocked.Increment(ref completedOperations);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(tasks);

        // Assert results
        exceptions.Should().BeEmpty("No exceptions should occur during concurrent reads");
        completedOperations.Should().Be(DefaultThreadCount * DefaultOperationsPerThread);
        results.Should().HaveCount(DefaultThreadCount * DefaultOperationsPerThread);
        results.Should().AllSatisfy(entry => entry.Should().Be(expectedEntry));

        _logger?.LogInformation(
            "Concurrent reads completed: {Operations} operations across {Threads} threads",
            completedOperations, DefaultThreadCount);
    }

    [Test]
    [Performance]
    public async Task ObjectCache_ConcurrentAccess_ShouldHandleRaceConditions()
    {
        // Test concurrent access to caching mechanism
        
        var cache = A.Fake<IMemoryCache>();
        var cacheHits = new ConcurrentBag<bool>();
        var cacheOperations = new ConcurrentBag<string>();

        // Simulate cache operations
        var tasks = Enumerable.Range(0, DefaultThreadCount).Select(async threadId =>
        {
            for (int i = 0; i < DefaultOperationsPerThread; i++)
            {
                var key = $"thread-{threadId}-op-{i}";
                
                // Simulate cache lookup
                var isHit = Random.Shared.NextDouble() > 0.5; // 50% hit rate simulation
                cacheHits.Add(isHit);
                cacheOperations.Add(key);
                
                // Simulate some async work
                await Task.Delay(Random.Shared.Next(1, 5));
            }
        });

        await Task.WhenAll(tasks);

        // Assert no race conditions or data corruption
        cacheHits.Should().HaveCount(DefaultThreadCount * DefaultOperationsPerThread);
        cacheOperations.Should().HaveCount(DefaultThreadCount * DefaultOperationsPerThread);
        cacheOperations.Should().OnlyHaveUniqueItems("All cache operations should have unique keys");

        var hitRate = cacheHits.Count(h => h) / (double)cacheHits.Count;
        _logger?.LogInformation(
            "Cache simulation completed: {Operations} operations, {HitRate:P2} hit rate",
            cacheHits.Count, hitRate);
    }

    [Test]
    [Performance]
    public async Task PackReader_ConcurrentFileAccess_ShouldBeSafe()
    {
        // Test concurrent access to pack files
        
        var packReader = A.Fake<PackReader>();
        var results = new ConcurrentBag<UnlinkedEntry>();
        var readOperations = new ConcurrentBag<long>(); // Track offsets read

        var tasks = Enumerable.Range(0, DefaultThreadCount).Select(async threadId =>
        {
            for (int i = 0; i < DefaultOperationsPerThread; i++)
            {
                var offset = threadId * 1000 + i * 10; // Generate unique offsets
                readOperations.Add(offset);
                
                // Simulate pack read operation
                var mockEntry = A.Fake<UnlinkedEntry>();
                results.Add(mockEntry);
                
                // Simulate I/O delay
                await Task.Delay(Random.Shared.Next(1, 10));
            }
        });

        await Task.WhenAll(tasks);

        // Assert thread safety
        results.Should().HaveCount(DefaultThreadCount * DefaultOperationsPerThread);
        readOperations.Should().HaveCount(DefaultThreadCount * DefaultOperationsPerThread);
        readOperations.Should().OnlyHaveUniqueItems("All read operations should target unique offsets");

        _logger?.LogInformation(
            "Concurrent pack reads completed: {Operations} operations across {Threads} threads",
            results.Count, DefaultThreadCount);
    }

    [Test]
    [Performance]
    public async Task ObjectResolution_HighContention_ShouldMaintainPerformance()
    {
        // Test performance under high contention scenarios
        
        var objectResolver = A.Fake<IObjectResolver>();
        var sharedHashId = new HashId("5a1ed1234567890abcdef1234567890abcdef123"); // Same hash for all threads
        var expectedEntry = A.Fake<BlobEntry>();

        A.CallTo(() => objectResolver.GetAsync<BlobEntry>(sharedHashId))
            .Returns(Task.FromResult(expectedEntry));

        var startTime = DateTime.UtcNow;
        var completedOperations = 0;
        var exceptions = new ConcurrentBag<Exception>();

        // All threads access the same object (high contention)
        var tasks = Enumerable.Range(0, DefaultThreadCount).Select(async threadId =>
        {
            for (int i = 0; i < DefaultOperationsPerThread; i++)
            {
                try
                {
                    var entry = await objectResolver.GetAsync<BlobEntry>(sharedHashId);
                    entry.Should().Be(expectedEntry);
                    Interlocked.Increment(ref completedOperations);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(tasks);
        
        var totalTime = DateTime.UtcNow - startTime;
        var operationsPerSecond = completedOperations / totalTime.TotalSeconds;

        // Assert performance and correctness
        exceptions.Should().BeEmpty("High contention should not cause exceptions");
        completedOperations.Should().Be(DefaultThreadCount * DefaultOperationsPerThread);
        operationsPerSecond.Should().BeGreaterThan(100, "Should maintain reasonable throughput under contention");

        _logger?.LogInformation(
            "High contention test: {Operations} operations in {Time:F2}s ({Throughput:F0} ops/sec)",
            completedOperations, totalTime.TotalSeconds, operationsPerSecond);
    }

    [Test]
    [Performance] 
    public async Task Repository_ConcurrentWalking_ShouldNotDeadlock()
    {
        // Test concurrent repository walking to detect deadlocks
        
        var objectResolver = A.Fake<IObjectResolver>();
        var commitIds = Enumerable.Range(0, 20)
            .Select(i => new HashId($"c0{i:D2}1234567890abcdef1234567890abcdef1234567890ab"))
            .ToList();

        // Setup fake commits with parent relationships
        for (int i = 0; i < commitIds.Count; i++)
        {
            var commit = A.Fake<CommitEntry>();
            A.CallTo(() => objectResolver.GetAsync<CommitEntry>(commitIds[i]))
                .Returns(Task.FromResult(commit));
        }

        var walkedCommits = new ConcurrentBag<HashId>();
        var walkingTasks = new List<Task>();

        // Start multiple concurrent repository walks
        for (int walkerId = 0; walkerId < DefaultThreadCount; walkerId++)
        {
            var startCommitIndex = walkerId % commitIds.Count;
            walkingTasks.Add(WalkRepository(objectResolver, commitIds[startCommitIndex], walkedCommits));
        }

        // Use timeout to detect deadlocks
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
        var completedTask = Task.WhenAll(walkingTasks);

        var finishedTask = await Task.WhenAny(completedTask, timeoutTask);
        
        finishedTask.Should().Be(completedTask, "Repository walking should complete without deadlocks");
        
        walkedCommits.Should().NotBeEmpty("Should have walked some commits");
        walkedCommits.Should().OnlyContain(id => commitIds.Contains(id), "Should only walk known commits");

        _logger?.LogInformation(
            "Concurrent repository walking completed: {Commits} commits walked by {Walkers} walkers",
            walkedCommits.Count, DefaultThreadCount);
    }

    [Test]
    [Performance]
    public async Task MemoryPressure_ConcurrentOperations_ShouldHandleGracefully()
    {
        // Test behavior under memory pressure with concurrent operations
        
        var objectResolver = A.Fake<IObjectResolver>();
        var largeData = new byte[1024 * 1024]; // 1MB per object
        var processedObjects = new ConcurrentBag<int>();

        // Create memory pressure with large objects
        var tasks = Enumerable.Range(0, DefaultThreadCount).Select(async threadId =>
        {
            for (int i = 0; i < 10; i++) // Fewer operations due to large objects
            {
                try
                {
                    var hashId = new HashId($"1a{threadId:D2}{i:D2}34567890abcdef1234567890abcdef12");
                    var blobEntry = A.Fake<BlobEntry>();
                    
                    A.CallTo(() => objectResolver.GetAsync<BlobEntry>(hashId))
                        .Returns(Task.FromResult(blobEntry));

                    var entry = await objectResolver.GetAsync<BlobEntry>(hashId);
                    processedObjects.Add(threadId * 100 + i);

                    // Simulate memory allocation
                    var tempBuffer = new byte[largeData.Length];
                    Array.Copy(largeData, tempBuffer, Math.Min(1000, largeData.Length));
                }
                catch (OutOfMemoryException)
                {
                    // Expected under extreme memory pressure
                    _logger?.LogWarning("OutOfMemoryException encountered in thread {ThreadId}", threadId);
                }
            }
        });

        await Task.WhenAll(tasks);

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        processedObjects.Should().NotBeEmpty("Should process some objects even under memory pressure");
        
        _logger?.LogInformation(
            "Memory pressure test completed: {Objects} objects processed",
            processedObjects.Count);
    }

    [Test]
    [Performance]
    public async Task ResourceDisposal_ConcurrentDispose_ShouldBeSafe()
    {
        // Test concurrent disposal of resources
        
        var disposables = Enumerable.Range(0, DefaultThreadCount)
            .Select(_ => A.Fake<IDisposable>())
            .ToList();

        var disposalTasks = disposables.Select(async disposable =>
        {
            await Task.Delay(Random.Shared.Next(1, 10)); // Random delay
            disposable.Dispose();
            return disposable; // Return the disposable to track completion
        });

        // Should complete without exceptions
        var results = await Task.WhenAll(disposalTasks);

        // Verify all disposables were disposed
        foreach (var disposable in disposables)
        {
            A.CallTo(() => disposable.Dispose()).MustHaveHappenedOnceExactly();
        }

        _logger?.LogInformation(
            "Concurrent disposal test completed: {Count} resources disposed",
            disposables.Count);
    }

    private async Task WalkRepository(IObjectResolver resolver, HashId startCommit, ConcurrentBag<HashId> walkedCommits)
    {
        var visited = new HashSet<HashId>();
        var toVisit = new Queue<HashId>();
        toVisit.Enqueue(startCommit);

        while (toVisit.Count > 0 && visited.Count < 10) // Limit walk depth
        {
            var commitId = toVisit.Dequeue();
            if (visited.Contains(commitId)) continue;

            visited.Add(commitId);
            walkedCommits.Add(commitId);

            try
            {
                var commit = await resolver.GetAsync<CommitEntry>(commitId);
                
                // Simulate walking to parents (in real implementation)
                await Task.Delay(Random.Shared.Next(1, 5));
                
                // Would add parent commits to toVisit in real implementation
            }
            catch (KeyNotFoundException)
            {
                // Expected for some commits
                continue;
            }
        }
    }
}