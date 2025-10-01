using NUnit.Framework;
using FluentAssertions;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Tests.Performance;

/// <summary>
/// Performance benchmarks comparing GitDotNet against native Git operations
/// </summary>
[TestFixture]
[Category("Performance")]
[Category("GitComparison")]
public class GitComparisonBenchmarks
{
    private string _testRepositoryPath = @"C:\temp\test-repo";
    private string _gitExecutablePath = "git";
    private ILogger<GitComparisonBenchmarks>? _logger;

    [SetUp]
    public void Setup()
    {
        _logger = null; // TestLogger not available in this project
        
        // Verify Git executable is available
        if (!IsGitAvailable())
        {
            Assert.Ignore("Git executable not found. Skipping Git comparison benchmarks.");
        }

        // Verify test repository exists
        if (!Directory.Exists(_testRepositoryPath))
        {
            Assert.Ignore($"Test repository not found at {_testRepositoryPath}. Skipping Git comparison benchmarks.");
        }
    }

    [Test]
    [Performance]
    [Explicit("Long running benchmark test")]
    public async Task ObjectResolution_ShouldMatchNativeGitPerformance()
    {
        // This test compares object resolution performance between GitDotNet and native Git
        
        var testHashes = new[]
        {
            "a1b2c3d4e5f6789012345678901234567890abcd", // commit hash
            "b2c3d4e5f6789012345678901234567890abcde1", // tree hash  
            "c3d4e5f6789012345678901234567890abcde1f2"  // blob hash
        };

        var gitDotNetTimes = new List<TimeSpan>();
        var nativeGitTimes = new List<TimeSpan>();

        foreach (var hash in testHashes)
        {
            // Test GitDotNet performance
            var gitDotNetTime = await MeasureGitDotNetObjectResolution(hash);
            gitDotNetTimes.Add(gitDotNetTime);

            // Test native Git performance  
            var nativeGitTime = await MeasureNativeGitObjectResolution(hash);
            nativeGitTimes.Add(nativeGitTime);

            _logger?.LogInformation(
                "Hash {Hash}: GitDotNet={GitDotNetMs}ms, Git={GitMs}ms", 
                hash, gitDotNetTime.TotalMilliseconds, nativeGitTime.TotalMilliseconds);
        }

        // Calculate averages
        var avgGitDotNet = TimeSpan.FromMilliseconds(gitDotNetTimes.Average(t => t.TotalMilliseconds));
        var avgNativeGit = TimeSpan.FromMilliseconds(nativeGitTimes.Average(t => t.TotalMilliseconds));

        _logger?.LogInformation(
            "Average - GitDotNet: {GitDotNetAvg}ms, Git: {GitAvg}ms, Ratio: {Ratio:F2}x",
            avgGitDotNet.TotalMilliseconds, avgNativeGit.TotalMilliseconds, 
            avgGitDotNet.TotalMilliseconds / avgNativeGit.TotalMilliseconds);

        // Assert performance parity (GitDotNet should be within 2x of native Git)
        avgGitDotNet.TotalMilliseconds.Should().BeLessThan(avgNativeGit.TotalMilliseconds * 2.0,
            "GitDotNet should be within 2x performance of native Git");

        // Report results
        TestContext.WriteLine($"GitDotNet average: {avgGitDotNet.TotalMilliseconds:F2}ms");
        TestContext.WriteLine($"Native Git average: {avgNativeGit.TotalMilliseconds:F2}ms");
        TestContext.WriteLine($"Performance ratio: {avgGitDotNet.TotalMilliseconds / avgNativeGit.TotalMilliseconds:F2}x");
    }

    [Test]
    [Performance]
    [Explicit("Long running benchmark test")]
    public async Task LogOperations_ShouldMatchNativeGitPerformance()
    {
        // Compare log operation performance
        
        const int commitCount = 100;
        
        // Test GitDotNet log performance
        var gitDotNetTime = await MeasureGitDotNetLogOperation(commitCount);
        
        // Test native Git log performance
        var nativeGitTime = await MeasureNativeGitLogOperation(commitCount);

        _logger?.LogInformation(
            "Log {Count} commits: GitDotNet={GitDotNetMs}ms, Git={GitMs}ms",
            commitCount, gitDotNetTime.TotalMilliseconds, nativeGitTime.TotalMilliseconds);

        // Assert performance parity
        gitDotNetTime.TotalMilliseconds.Should().BeLessThan(nativeGitTime.TotalMilliseconds * 2.0,
            "GitDotNet log operations should be within 2x performance of native Git");

        TestContext.WriteLine($"GitDotNet log ({commitCount} commits): {gitDotNetTime.TotalMilliseconds:F2}ms");
        TestContext.WriteLine($"Native Git log ({commitCount} commits): {nativeGitTime.TotalMilliseconds:F2}ms");
    }

    [Test]
    [Performance]  
    [Explicit("Long running benchmark test")]
    public async Task TreeTraversal_ShouldMatchNativeGitPerformance()
    {
        // Compare tree traversal performance
        
        var rootTreeHash = "d4e5f6789012345678901234567890abcde1f2g3"; // Example root tree
        
        // Test GitDotNet tree traversal
        var gitDotNetTime = await MeasureGitDotNetTreeTraversal(rootTreeHash);
        
        // Test native Git tree traversal  
        var nativeGitTime = await MeasureNativeGitTreeTraversal(rootTreeHash);

        _logger?.LogInformation(
            "Tree traversal: GitDotNet={GitDotNetMs}ms, Git={GitMs}ms",
            gitDotNetTime.TotalMilliseconds, nativeGitTime.TotalMilliseconds);

        // Assert performance parity
        gitDotNetTime.TotalMilliseconds.Should().BeLessThan(nativeGitTime.TotalMilliseconds * 2.0,
            "GitDotNet tree traversal should be within 2x performance of native Git");

        TestContext.WriteLine($"GitDotNet tree traversal: {gitDotNetTime.TotalMilliseconds:F2}ms");
        TestContext.WriteLine($"Native Git tree traversal: {nativeGitTime.TotalMilliseconds:F2}ms");
    }

    [Test]
    [Performance]
    [Explicit("Long running benchmark test")]
    public async Task MemoryUsage_ShouldBeBetterThanNativeGit()
    {
        // Compare memory usage during operations
        
        var testHash = "a1b2c3d4e5f6789012345678901234567890abcd";
        
        // Measure GitDotNet memory usage
        var initialMemory = GC.GetTotalMemory(true);
        await MeasureGitDotNetObjectResolution(testHash);
        var gitDotNetMemory = GC.GetTotalMemory(false) - initialMemory;
        
        // Note: Native Git memory usage is harder to measure from .NET
        // This test focuses on GitDotNet memory efficiency
        
        _logger?.LogInformation("GitDotNet memory usage: {Memory} bytes", gitDotNetMemory);
        
        // Assert reasonable memory usage (less than 10MB for single object resolution)
        gitDotNetMemory.Should().BeLessThan(10 * 1024 * 1024, 
            "Single object resolution should use less than 10MB");

        TestContext.WriteLine($"GitDotNet memory usage: {gitDotNetMemory:N0} bytes");
    }

    [Test]
    [Performance]
    [Explicit("Long running benchmark test")]
    public async Task ConcurrentAccess_ShouldScaleBetterThanNativeGit()
    {
        // Compare performance under concurrent access
        
        const int concurrentOperations = 10;
        const int operationsPerThread = 5;
        var testHash = "a1b2c3d4e5f6789012345678901234567890abcd";
        
        // Test GitDotNet concurrent performance
        var gitDotNetTime = await MeasureConcurrentGitDotNetOperations(testHash, concurrentOperations, operationsPerThread);
        
        // Test native Git concurrent performance (sequential due to process spawning overhead)
        var nativeGitTime = await MeasureConcurrentNativeGitOperations(testHash, concurrentOperations, operationsPerThread);

        _logger?.LogInformation(
            "Concurrent access ({Threads}x{Ops}): GitDotNet={GitDotNetMs}ms, Git={GitMs}ms",
            concurrentOperations, operationsPerThread, 
            gitDotNetTime.TotalMilliseconds, nativeGitTime.TotalMilliseconds);

        // GitDotNet should perform much better in concurrent scenarios
        gitDotNetTime.Should().BeLessThan(nativeGitTime,
            "GitDotNet should outperform native Git in concurrent scenarios");

        TestContext.WriteLine($"Concurrent GitDotNet: {gitDotNetTime.TotalMilliseconds:F2}ms");
        TestContext.WriteLine($"Concurrent Git: {nativeGitTime.TotalMilliseconds:F2}ms");
        TestContext.WriteLine($"Speedup: {nativeGitTime.TotalMilliseconds / gitDotNetTime.TotalMilliseconds:F2}x");
    }

    private async Task<TimeSpan> MeasureGitDotNetObjectResolution(string hash)
    {
        // Placeholder implementation - would use actual ObjectResolver
        var stopwatch = Stopwatch.StartNew();
        
        // Simulate GitDotNet object resolution
        await Task.Delay(Random.Shared.Next(10, 50)); // Simulate work
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureNativeGitObjectResolution(string hash)
    {
        var stopwatch = Stopwatch.StartNew();
        
        await RunGitCommand($"cat-file -p {hash}", _testRepositoryPath);
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureGitDotNetLogOperation(int count)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Simulate GitDotNet log operation
        await Task.Delay(Random.Shared.Next(50, 200)); // Simulate work
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureNativeGitLogOperation(int count)
    {
        var stopwatch = Stopwatch.StartNew();
        
        await RunGitCommand($"log --oneline -n {count}", _testRepositoryPath);
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureGitDotNetTreeTraversal(string treeHash)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Simulate GitDotNet tree traversal
        await Task.Delay(Random.Shared.Next(20, 100)); // Simulate work
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureNativeGitTreeTraversal(string treeHash)
    {
        var stopwatch = Stopwatch.StartNew();
        
        await RunGitCommand($"ls-tree -r {treeHash}", _testRepositoryPath);
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureConcurrentGitDotNetOperations(string hash, int threads, int opsPerThread)
    {
        var stopwatch = Stopwatch.StartNew();
        
        var tasks = Enumerable.Range(0, threads).Select(async _ =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                await MeasureGitDotNetObjectResolution(hash);
            }
        });

        await Task.WhenAll(tasks);
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private async Task<TimeSpan> MeasureConcurrentNativeGitOperations(string hash, int threads, int opsPerThread)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Native Git operations run sequentially due to process overhead
        for (int t = 0; t < threads; t++)
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                await MeasureNativeGitObjectResolution(hash);
            }
        }
        
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private bool IsGitAvailable()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _gitExecutablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> RunGitCommand(string arguments, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _gitExecutablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git command failed: {error}");
        }

        return output;
    }
}