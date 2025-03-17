using System.IO.Abstractions;

namespace GitDotNet.Tools;

internal delegate IRepositoryLocker RepositoryLockerFactory(string path);

internal class RepositoryLocker(string path, IFileSystem fileSystem) : IRepositoryLocker
{
    private readonly AsyncLocal<bool> _lockTaken = new();
    private readonly string _lockFilePath = fileSystem.Path.Combine(path, "index.lock");

    public IDisposable GetLock()
    {
        // Allows reentrancy
        if (_lockTaken.Value)
        {
            return Disposable.Empty;
        }

        // Create the lock file
        using (fileSystem.File.Create(_lockFilePath)) { }

        _lockTaken.Value = true;
        return new Disposable(() =>
        {

            _lockTaken.Value = false;
            if (fileSystem.File.Exists(_lockFilePath))
            {
                fileSystem.File.Delete(_lockFilePath);
                Console.WriteLine($"Lock released: {_lockFilePath}");
            }
        });
    }

    internal sealed class Disposable(Action Action) : IDisposable
    {
        internal static IDisposable Empty { get; } = new Disposable(() => { });

        private readonly Action _dispose = Action ?? throw new ArgumentNullException(nameof(Action));
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _dispose();
            }
        }
    }
}

internal interface IRepositoryLocker
{
    IDisposable GetLock();
}