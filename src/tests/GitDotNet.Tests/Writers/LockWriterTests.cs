using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Writers;
using Microsoft.Extensions.Logging.Abstractions;
using FakeItEasy;

namespace GitDotNet.Tests.Writers;

public class LockWriterTests
{
    private MockFileSystem _fileSystem;
    private IRepositoryInfo _repositoryInfo;

    [SetUp]
    public void Setup()
    {
        _fileSystem = new MockFileSystem();
        _fileSystem.AddDirectory(".git");
        _repositoryInfo = A.Fake<IRepositoryInfo>();
        A.CallTo(() => _repositoryInfo.Path).Returns("/.git");
    }

    [Test]
    public async Task DoAsync_WithResult_ExecutesOperationWithLock()
    {
        // Arrange
        var expectedLockPath = "/.git/index.lock";
        var expectedResult = "operation result";
        
        var lockWriter = new LockWriter(_repositoryInfo, _fileSystem);

        // Act
        var result = await lockWriter.DoAsync(async () =>
        {
            // Verify lock file exists during operation
            _fileSystem.File.Exists(expectedLockPath).Should().BeTrue("Lock file should exist during operation");
            await Task.Delay(1); // Simulate async work
            return expectedResult;
        });

        // Assert
        using (new AssertionScope())
        {
            result.Should().Be(expectedResult);
            _fileSystem.File.Exists(expectedLockPath).Should().BeFalse("Lock file should be cleaned up after operation");
        }
    }

    [Test]
    public async Task DoAsync_WithoutResult_ExecutesOperationWithLock()
    {
        // Arrange
        var expectedLockPath = "/.git/index.lock";
        var operationExecuted = false;
        
        var lockWriter = new LockWriter(_repositoryInfo, _fileSystem);

        // Act
        await lockWriter.DoAsync(async () =>
        {
            // Verify lock file exists during operation
            _fileSystem.File.Exists(expectedLockPath).Should().BeTrue("Lock file should exist during operation");
            await Task.Delay(1); // Simulate async work
            operationExecuted = true;
        });

        // Assert
        using (new AssertionScope())
        {
            operationExecuted.Should().BeTrue("Operation should have been executed");
            _fileSystem.File.Exists(expectedLockPath).Should().BeFalse("Lock file should be cleaned up after operation");
        }
    }

    [Test]
    public async Task DoAsync_ThrowsWhenLockFileAlreadyExists()
    {
        // Arrange
        var expectedLockPath = "/.git/index.lock";
        _fileSystem.AddFile(expectedLockPath, new MockFileData("existing lock"));
        
        var lockWriter = new LockWriter(_repositoryInfo, _fileSystem);

        // Act & Assert
        var action = async () => await lockWriter.DoAsync(async () =>
        {
            await Task.Delay(1);
            return "result";
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Test]
    public async Task DoAsync_CleansUpLockFileOnException()
    {
        // Arrange
        var expectedLockPath = "/.git/index.lock";
        var expectedException = new InvalidOperationException("Test exception");
        
        var lockWriter = new LockWriter(_repositoryInfo, _fileSystem);

        // Act & Assert
        var action = async () => await lockWriter.DoAsync(async () =>
        {
            // Verify lock file exists during operation
            _fileSystem.File.Exists(expectedLockPath).Should().BeTrue("Lock file should exist during operation");
            await Task.Delay(1);
            throw expectedException;
        });

        var thrownException = await action.Should().ThrowAsync<InvalidOperationException>();
        thrownException.Which.Should().Be(expectedException);

        // Verify cleanup
        _fileSystem.File.Exists(expectedLockPath).Should().BeFalse("Lock file should be cleaned up even after exception");
    }

    [Test]
    public async Task DoAsync_WithLogger_LogsOperations()
    {
        // Arrange
        var logger = NullLogger<LockWriter>.Instance;
        
        var lockWriter = new LockWriter(_repositoryInfo, _fileSystem, logger);

        // Act & Assert - just verify it doesn't throw with logger
        var result = await lockWriter.DoAsync(async () =>
        {
            await Task.Delay(1);
            return "logged operation";
        });

        result.Should().Be("logged operation");
    }

    [Test]
    public async Task DoAsync_ThrowsOnNullOperation()
    {
        // Arrange
        var lockWriter = new LockWriter(_repositoryInfo, _fileSystem);

        // Act & Assert
        var action = async () => await lockWriter.DoAsync((Func<Task<string>>)null!);

        await action.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task DoAsync_SupportsMultipleConcurrentOperationsWithSeparateInstances()
    {
        // Arrange
        _fileSystem.AddDirectory(".git1");
        _fileSystem.AddDirectory(".git2");
        var repositoryInfo1 = A.Fake<IRepositoryInfo>();
        var repositoryInfo2 = A.Fake<IRepositoryInfo>();
        A.CallTo(() => repositoryInfo1.Path).Returns("/.git1");
        A.CallTo(() => repositoryInfo2.Path).Returns("/.git2");
        
        var results = new List<string>();

        var lockWriter1 = new LockWriter(repositoryInfo1, _fileSystem);
        var lockWriter2 = new LockWriter(repositoryInfo2, _fileSystem);

        // Act - run operations concurrently on different repositories
        var task1 = lockWriter1.DoAsync(async () =>
        {
            await Task.Delay(50);
            results.Add("operation1");
            return "result1";
        });

        var task2 = lockWriter2.DoAsync(async () =>
        {
            await Task.Delay(30);
            results.Add("operation2");
            return "result2";
        });

        var actualResults = await Task.WhenAll(task1, task2);

        // Assert
        using (new AssertionScope())
        {
            actualResults.Should().Contain("result1");
            actualResults.Should().Contain("result2");
            results.Should().HaveCount(2);
            _fileSystem.File.Exists("/.git1/index.lock").Should().BeFalse();
            _fileSystem.File.Exists("/.git2/index.lock").Should().BeFalse();
        }
    }

    [Test]
    public async Task DoAsync_PreventsMultipleConcurrentOperationsOnSameRepository()
    {
        // Arrange
        var operation1Started = new TaskCompletionSource<bool>();
        var operation2CanContinue = new TaskCompletionSource<bool>();

        var lockWriter1 = new LockWriter(_repositoryInfo, _fileSystem);
        var lockWriter2 = new LockWriter(_repositoryInfo, _fileSystem);

        // Start first operation that will hold the lock
        var task1 = lockWriter1.DoAsync(async () =>
        {
            operation1Started.SetResult(true);
            await operation2CanContinue.Task; // Wait for signal
            return "result1";
        });

        // Wait for first operation to acquire lock
        await operation1Started.Task;

        // Try to start second operation on same repository - should fail
        var task2Function = async () => await lockWriter2.DoAsync(async () =>
        {
            await Task.Delay(1);
            return "result2";
        });

        // Second operation should fail immediately
        await task2Function.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");

        // Complete first operation
        operation2CanContinue.SetResult(true);
        var result1 = await task1;

        // Assert
        result1.Should().Be("result1");
    }
}