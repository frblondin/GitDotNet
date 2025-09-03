using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;

internal delegate LockWriter LockWriterFactory(IRepositoryInfo repositoryInfo);

/// <summary>
/// Manages Git lock files for atomic write operations. Creates and cleans up lock files automatically
/// around the execution of a delegate function.
/// </summary>
internal sealed class LockWriter(IRepositoryInfo repositoryInfo, IFileSystem fileSystem, ILogger<LockWriter>? logger = null)
{
    private readonly string _lockFilePath = fileSystem.Path.Combine(repositoryInfo.Path, "index.lock");

    /// <summary>
    /// Executes an operation with a lock file, ensuring atomic access to the target file.
    /// The lock file is automatically created before execution and cleaned up afterwards.
    /// </summary>
    /// <param name="operation">The operation to execute while holding the lock.</param>
    /// <exception cref="ArgumentNullException">Thrown when operation is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a lock file already exists.</exception>
    public async Task DoAsync(Func<Task> operation)
    {
        await DoAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return 0; // Dummy return value
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an operation with a lock file, ensuring atomic access to the target file.
    /// The lock file is automatically created before execution and cleaned up afterwards.
    /// </summary>
    /// <typeparam name="TResult">The return type of the operation.</typeparam>
    /// <param name="operation">The operation to execute while holding the lock.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when operation is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a lock file already exists.</exception>
    public async Task<TResult> DoAsync<TResult>(Func<Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        logger?.LogDebug("Creating lock file: {LockFilePath}", _lockFilePath);

        // Check if lock file already exists
        if (fileSystem.File.Exists(_lockFilePath))
        {
            throw new InvalidOperationException($"Lock file already exists: {_lockFilePath}. Another operation may be in progress.");
        }

        await CreateLockFile();

        logger?.LogDebug("Successfully created lock file: {LockFilePath}", _lockFilePath);

#pragma warning disable S2139 // Exceptions should be either logged or rethrown but not both
        try
        {
            // Execute the operation
            var result = await operation().ConfigureAwait(false);

            logger?.LogDebug("Operation completed successfully for: {TargetFilePath}", _lockFilePath);
            return result;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Operation failed for: {TargetFilePath}", _lockFilePath);
            throw;
        }
        finally
        {
            // Always clean up the lock file
            try
            {
                if (fileSystem.File.Exists(_lockFilePath))
                {
                    fileSystem.File.Delete(_lockFilePath);
                    logger?.LogDebug("Cleaned up lock file: {LockFilePath}", _lockFilePath);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to delete lock file: {LockFilePath}", _lockFilePath);
            }
        }
#pragma warning restore S2139 // Exceptions should be either logged or rethrown but not both
    }

    private async Task CreateLockFile()
    {
        await using var lockFileStream = fileSystem.File.Create(_lockFilePath);
    }
}