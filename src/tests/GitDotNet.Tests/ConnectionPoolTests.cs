using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using FluentAssertions.Execution;

namespace GitDotNet.Tests;

public class ConnectionPoolTests
{
    [Test]
    public void AcquireShouldReturnLockForRead()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.Directory.CreateDirectory(path);

        // Act
        using var lockObj = _connectionPool.Acquire(path, isWrite: false);

        // Assert
        using (new AssertionScope())
        {
            lockObj.Should().NotBeNull();
            lockObj.Should().BeOfType<ConnectionPool.Lock>();
        }
    }

    [Test]
    public void AcquireShouldReturnLockForWrite()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.Directory.CreateDirectory(path);

        // Act
        using var lockObj = _connectionPool.Acquire(path, isWrite: true);

        // Assert
        using (new AssertionScope())
        {
            lockObj.Should().NotBeNull();
            lockObj.Should().BeOfType<ConnectionPool.Lock>();
        }
    }

    [Test]
    public void LockShouldCreateLockFileForWrite()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);

        // Act
        using var lockObj = _connectionPool.Acquire(path, isWrite: true);

        // Assert
        _fileSystem.FileExists($"{path}/index.lock").Should().BeTrue();
    }

    [Test]
    public void LockShouldNotCreateLockFileForRead()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);

        // Act
        using var lockObj = _connectionPool.Acquire(path, isWrite: false);

        // Assert
        _fileSystem.FileExists($"{path}/index.lock").Should().BeFalse();
    }

    [Test]
    public void LockShouldDeleteLockFileOnDispose()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);

        // Act
        using (_connectionPool.Acquire(path, isWrite: true))
        {
            // Assert inside using block
            _fileSystem.FileExists($"{path}/index.lock").Should().BeTrue();
        }

        // Assert after dispose
        _fileSystem.FileExists($"{path}/index.lock").Should().BeFalse();
    }

    [Test]
    public void LockShouldThrowIfDisposed()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.Directory.CreateDirectory(path);
        using var lockObj = _connectionPool.Acquire(path, isWrite: true);
        lockObj.Dispose();

        // Act
        var action = () => lockObj.ExecuteWithTemporaryLockReleaseAsync(() => Task.CompletedTask).Wait();

        // Assert
        action.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void LockShouldThrowIfWriteAttemptedOnReadOnlyLock()
    {
        // Arrange
        var path = "/test/repo";
        using var lockObj = _connectionPool.Acquire(path, isWrite: false);

        // Act
        var action = () => lockObj.ExecuteWithTemporaryLockReleaseAsync(() => Task.CompletedTask).Wait();

        // Assert
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot write using a read-only connection.");
    }

    [Test]
    public void AcquireShouldNormalizePath()
    {
        // Arrange
        var path = @"C:\test\repo";

        // Act
        using var lockObj = _connectionPool.Acquire(path, isWrite: false);

        // Assert
        lockObj.Should().NotBeNull();
        lockObj.Path.Should().Contain("C:/test/repo");
    }

    [Test]
    public async Task WriteAcquireShouldWaitUntilPendingReadsAreCompleted()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);

        using var readLock1 = _connectionPool.Acquire(path, isWrite: false);
        using var readLock2 = _connectionPool.Acquire(path, isWrite: false);

        var writeTask = Task.Run(() =>
        {
            using var writeLock = _connectionPool.Acquire(path, isWrite: true);
            _fileSystem.FileExists($"{path}/index.lock").Should().BeTrue();
        });

        // Act
        await Task.Delay(100); // Ensure write task is waiting
        readLock1.Dispose();
        readLock2.Dispose();

        // Assert
        await writeTask; // Should complete after reads are released
    }

    [Test]
    public async Task ReadAcquireShouldWaitUntilPendingWriteIsCompleted()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);

        using var writeLock = _connectionPool.Acquire(path, isWrite: true);

        var readTask = Task.Run(() =>
        {
            using var readLock = _connectionPool.Acquire(path, isWrite: false);
            _fileSystem.FileExists($"{path}/index.lock").Should().BeFalse();
        });

        // Act
        await Task.Delay(100); // Ensure read task is waiting
        writeLock.Dispose();

        // Assert
        await readTask; // Should complete after write is released
    }

    [Test]
    public void MultipleReadsAtATimeAreAllowed()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);

        // Act
        using var readLock1 = _connectionPool.Acquire(path, isWrite: false);
        using var readLock2 = _connectionPool.Acquire(path, isWrite: false);

        // Assert
        readLock1.Should().NotBeNull();
        readLock2.Should().NotBeNull();
    }

    [Test]
    public void OnlyOneWriteAtATimeIsAllowed()
    {
        // Arrange
        var path = "/test/repo";
        _fileSystem.AddDirectory(path);
        var tokenSource = new CancellationTokenSource();

        // Act
        using var writeLock = _connectionPool.Acquire(path, isWrite: true);
        var action = () => _connectionPool.Acquire(path, isWrite: true, tokenSource.Token);
        tokenSource.CancelAfter(TimeSpan.FromMicroseconds(100));

        // Assert
        action.Should().Throw<TaskCanceledException>();
    }

    private MockFileSystem _fileSystem;
    private ConnectionPool _connectionPool;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystem();
        _connectionPool = new ConnectionPool(_fileSystem);
    }
}
