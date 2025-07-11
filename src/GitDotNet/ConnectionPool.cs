using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using Nito.AsyncEx;

namespace GitDotNet;
internal partial class ConnectionPool(IFileSystem fileSystem)
{
    private static readonly ConcurrentDictionary<string, AsyncReaderWriterLock> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public virtual Lock Acquire(string path, bool isWrite, CancellationToken? token = null)
    {
        var normalized = fileSystem.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        var readerWriteLock = _locks.GetOrAdd(path, _ => new AsyncReaderWriterLock());
        var @lock = isWrite ?
            readerWriteLock.WriterLock(token ?? CancellationToken.None) :
            readerWriteLock.ReaderLock(token ?? CancellationToken.None);
        return new Lock(normalized, @lock, isWrite, fileSystem);
    }

    internal sealed class Lock : IDisposable
    {
        private readonly IDisposable? _lock;
        private readonly IFileSystem _fileSystem;
        private readonly string? _lockFilePath;
        private bool _disposedValue;

        public Lock(string? path, IDisposable? @lock, bool isWrite, IFileSystem fileSystem)
        {
            Path = path;
            _lock = @lock;
            IsWrite = isWrite;
            _fileSystem = fileSystem;
            _lockFilePath = path != null ? _fileSystem.Path.Combine(path, "index.lock") : null;

            if (isWrite)
            {
                CreateLockFile();
            }
            else
            {
                WaitUntilLockFileDoesNotExist();
            }
        }

        public string? Path { get; }

        public bool IsWrite { get; }

        public bool CanRead => IsWrite || !LockFileExists;

        public bool LockFileExists => _lockFilePath != null && _fileSystem.File.Exists(_lockFilePath);

        private void CreateLockFile()
        {
            if (_lockFilePath == null) return;
            WaitUntilLockFileDoesNotExist();
            using var lockFile = _fileSystem.File.Create(_lockFilePath);
        }

        public void WaitUntilCanRead()
        {
            if (!IsWrite) WaitUntilLockFileDoesNotExist();
        }

        public void WaitUntilLockFileDoesNotExist()
        {
            ObjectDisposedException.ThrowIf(_disposedValue, nameof(Lock));
            if (_lockFilePath == null) return;
            if (!SpinWait.SpinUntil(() => !LockFileExists, TimeSpan.FromMinutes(1)))
            {
                throw new TimeoutException("Unable to acquire index.lock file.");
            }
        }

        private void DeleteLockFile()
        {
            if (_lockFilePath == null) return;
            if (!LockFileExists)
            {
                throw new InvalidOperationException("index.lock file was expected to exist.");
            }
            _fileSystem.File.Delete(_lockFilePath);
        }

        public async Task ExecuteWithTemporaryLockReleaseAsync(Func<Task> action)
        {
            ObjectDisposedException.ThrowIf(_disposedValue, nameof(Lock));
            if (!IsWrite)
            {
                throw new InvalidOperationException("Cannot write using a read-only connection.");
            }

            DeleteLockFile();
            try
            {
                await action();
            }
            finally
            {
                CreateLockFile();
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (IsWrite) DeleteLockFile();
                _lock?.Dispose();

                _disposedValue = true;
            }
        }

        /// <summary>Finalizes an instance of the <see cref="GitConnection"/> class.</summary>
        [ExcludeFromCodeCoverage]
        ~Lock()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
