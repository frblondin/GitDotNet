using System.IO.Abstractions;

namespace GitDotNet.Tools;

internal delegate IRepositoryLocker RepositoryLockerFactory(string path);

internal sealed class RepositoryLocker : IRepositoryLocker
{
    internal static RepositoryLocker Empty { get; } = new RepositoryLocker(string.Empty, new FileSystem());

    private readonly IFileSystem _fileSystem;
    private readonly string? _lockFilePath;
    private bool _disposedValue;

    public RepositoryLocker(string? path, IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _lockFilePath = path != null ? _fileSystem.Path.Combine(path, "index.lock") : null;

        CreateLockFile();
    }

    private void CreateLockFile()
    {
        if (_lockFilePath == null) return;
        using var lockFile = _fileSystem.File.Create(_lockFilePath);
    }

    private void DeleteLockFile()
    {
        if (_lockFilePath != null && _fileSystem.File.Exists(_lockFilePath))
        {
            _fileSystem.File.Delete(_lockFilePath);
        }
    }

    public void ExecuteWithTemporaryLockRelease(Action action)
    {
        DeleteLockFile();

        try
        {
            action();
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
            if (disposing)
            {
                DeleteLockFile();
            }

            _disposedValue = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

internal interface IRepositoryLocker : IDisposable
{
    void ExecuteWithTemporaryLockRelease(Action action);
}