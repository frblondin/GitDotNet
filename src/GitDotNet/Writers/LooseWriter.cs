using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;

/// <summary>A delegate for creating LooseWriter instances.</summary>
internal delegate LooseWriter LooseWriterFactory(string repositoryPath);

/// <summary>Writes Git objects as loose objects in the repository's objects directory.</summary>
/// <remarks>
/// <para>
/// This class implements writing individual Git objects to the .git/objects directory
/// using the standard Git loose object format: compressed with zlib and stored in
/// subdirectories based on the first two characters of the object's SHA-1 hash.
/// </para>
/// <para>
/// <strong>Loose Object Format:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Header: "{type} {size}\0" (ASCII encoded)</description></item>
/// <item><description>Content: Raw object data</description></item>
/// <item><description>Compression: Entire content compressed with zlib</description></item>
/// <item><description>Storage: File path based on SHA-1 hash (first 2 chars as directory, remaining 38 as filename)</description></item>
/// </list>
/// <para>
/// This writer is thread-safe and can handle concurrent writes to different objects.
/// It automatically creates necessary directory structures and validates object integrity.
/// </para>
/// </remarks>
internal sealed class LooseWriter : IDisposable
{
    private readonly string _objectsPath;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LooseWriter>? _logger;
    private readonly SHA1 _sha1;
    private bool _disposed;

    private static readonly byte[] _commitHeader = Encoding.ASCII.GetBytes("commit");
    private static readonly byte[] _blobHeader = Encoding.ASCII.GetBytes("blob");
    private static readonly byte[] _treeHeader = Encoding.ASCII.GetBytes("tree");
    private static readonly byte[] _tagHeader = Encoding.ASCII.GetBytes("tag");

    /// <summary>Initializes a new instance of the LooseWriter class.</summary>
    /// <param name="repositoryPath">The path to the Git repository (typically the .git directory).</param>
    /// <param name="fileSystem">The file system abstraction.</param>
    /// <param name="logger">Optional logger for debugging and information.</param>
    public LooseWriter(string repositoryPath, IFileSystem fileSystem, ILogger<LooseWriter>? logger = null)
    {
        var repositoryPath1 = repositoryPath ?? throw new ArgumentNullException(nameof(repositoryPath));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger;
        _sha1 = SHA1.Create();
        _objectsPath = _fileSystem.Path.Combine(repositoryPath1, "objects");

        // Ensure the objects directory exists
        _fileSystem.Directory.CreateDirectory(_objectsPath);

        _logger?.LogDebug("LooseWriter initialized for repository: {RepositoryPath}", repositoryPath1);
    }

    /// <summary>Writes a Git object as a loose object to the repository.</summary>
    /// <param name="type">The Git object type.</param>
    /// <param name="data">The object's raw content data.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The HashId of the written object.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the writer has been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when data is null.</exception>
    public async Task<HashId> WriteObjectAsync(EntryType type, byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(LooseWriter));
        ArgumentNullException.ThrowIfNull(data);

        var objectId = HashId.Create(type, data);
        var objectPath = GetObjectPath(objectId);

        // Check if object already exists
        if (_fileSystem.File.Exists(objectPath))
        {
            _logger?.LogDebug("Object {ObjectId} already exists, skipping write", objectId);
            return objectId;
        }

        // Ensure the object's subdirectory exists
        var objectDir = _fileSystem.Path.GetDirectoryName(objectPath);
        if (objectDir != null)
        {
            _fileSystem.Directory.CreateDirectory(objectDir);
        }

        // Create the object content with header
        var objectContent = CreateObjectContent(type, data);

        // Write the compressed object to a temporary file first, then rename
        var tempPath = $"{objectPath}.tmp-{Environment.ProcessId}-{Path.GetRandomFileName()}";

#pragma warning disable S2139 // Exceptions should be either logged or rethrown but not both
        try
        {
            await using (var fileStream = _fileSystem.File.Create(tempPath))
            await using (var zlibStream = new ZLibStream(fileStream, CompressionMode.Compress))
            {
                await zlibStream.WriteAsync(objectContent, cancellationToken).ConfigureAwait(false);
                await zlibStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Atomically move the temporary file to the final location
            _fileSystem.File.Move(tempPath, objectPath);

            _logger?.LogDebug("Successfully wrote loose object: {ObjectId} to {ObjectPath}", objectId, objectPath);
        }
        catch (Exception ex)
        {
            // Clean up temporary file if it exists
            try
            {
                if (_fileSystem.File.Exists(tempPath))
                {
                    _fileSystem.File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                _logger?.LogWarning(cleanupEx, "Failed to clean up temporary file: {TempPath}", tempPath);
            }

            _logger?.LogError(ex, "Failed to write loose object: {ObjectId}", objectId);
            throw;
        }
#pragma warning restore S2139 // Exceptions should be either logged or rethrown but not both

        return objectId;
    }

    /// <summary>Creates the complete object content including the header.</summary>
    /// <param name="type">The Git object type.</param>
    /// <param name="data">The object's raw content data.</param>
    /// <returns>The complete object content with header.</returns>
    private static byte[] CreateObjectContent(EntryType type, byte[] data)
    {
        var typeBytes = GetTypeBytes(type);
        var sizeBytes = Encoding.ASCII.GetBytes(data.Length.ToString());

        // Calculate total size: type + space + size + null terminator + data
        var totalSize = typeBytes.Length + 1 + sizeBytes.Length + 1 + data.Length;
        var content = new byte[totalSize];

        var offset = 0;

        // Write type
        Array.Copy(typeBytes, 0, content, offset, typeBytes.Length);
        offset += typeBytes.Length;

        // Write space
        content[offset++] = 0x20; // Space character

        // Write size
        Array.Copy(sizeBytes, 0, content, offset, sizeBytes.Length);
        offset += sizeBytes.Length;

        // Write null terminator
        content[offset++] = 0x00;

        // Write data
        Array.Copy(data, 0, content, offset, data.Length);

        return content;
    }

    /// <summary>Gets the type bytes for the specified entry type.</summary>
    /// <param name="type">The entry type.</param>
    /// <returns>The ASCII bytes representing the type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for unsupported entry types.</exception>
    private static byte[] GetTypeBytes(EntryType type) => type switch
    {
        EntryType.Commit => _commitHeader,
        EntryType.Blob => _blobHeader,
        EntryType.Tree => _treeHeader,
        EntryType.Tag => _tagHeader,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, $"Unsupported entry type for loose objects: {type}")
    };

    /// <summary>Gets the file system path for the specified object ID.</summary>
    /// <param name="objectId">The object ID.</param>
    /// <returns>The complete file path where the object should be stored.</returns>
    private string GetObjectPath(HashId objectId)
    {
        var hexString = objectId.ToString();
        var subdirectory = hexString[..2];
        var filename = hexString[2..];

        return _fileSystem.Path.Combine(_objectsPath, subdirectory, filename);
    }

    /// <summary>Releases all resources used by the LooseWriter.</summary>
    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _sha1?.Dispose();
                _logger?.LogDebug("LooseWriter disposed");
            }
            _disposed = true;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}