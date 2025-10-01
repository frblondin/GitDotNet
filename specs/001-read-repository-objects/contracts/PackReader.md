# PackReader Contract

```csharp
using System;
using System.Threading.Tasks;

namespace GitDotNet.Readers
{
    /// <summary>
    /// Factory delegate for creating PackReader instances.
    /// </summary>
    /// <param name="path">Path to the pack file.</param>
    /// <returns>A new PackReader instance.</returns>
    internal delegate PackReader PackReaderFactory(string path);

    /// <summary>
    /// Reads Git objects from pack files with delta reconstruction.
    /// </summary>
    internal class PackReader : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the PackReader class.
        /// </summary>
        /// <param name="path">Path to the pack file.</param>
        /// <param name="offsetStreamReaderFactory">Factory for creating offset stream readers.</param>
        /// <param name="logger">Optional logger instance.</param>
        PackReader(string path, FileOffsetStreamReaderFactory offsetStreamReaderFactory, ILogger<PackReader>? logger = null);

        /// <summary>
        /// Reads a Git object from the pack file at the specified offset.
        /// </summary>
        /// <param name="id">The hash ID of the object.</param>
        /// <param name="offset">The offset within the pack file.</param>
        /// <param name="dependentEntryProvider">Function to resolve dependent objects for delta reconstruction.</param>
        /// <returns>The unlinked entry containing object data.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the reader has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Thrown when unexpected end of stream is encountered.</exception>
        /// <exception cref="NotImplementedException">Thrown for unknown object types.</exception>
        Task<UnlinkedEntry> ReadAsync(HashId id, long offset, Func<HashId, Task<UnlinkedEntry>> dependentEntryProvider);

        /// <summary>
        /// Gets an object by offset with caching.
        /// </summary>
        /// <param name="offset">The offset within the pack file.</param>
        /// <param name="provider">Function to provide the object if not cached.</param>
        /// <returns>The unlinked entry containing object data.</returns>
        Task<UnlinkedEntry> GetByOffsetAsync(long offset, Func<Task<UnlinkedEntry>> provider);

        /// <summary>
        /// Extracts variable-length offset encoding from stream.
        /// </summary>
        /// <param name="stream">Stream to read from.</param>
        /// <returns>The decoded offset value.</returns>
        /// <exception cref="EndOfStreamException">Thrown when unexpected end of stream is encountered.</exception>
        static long ExtractOffset(Stream stream);

        /// <summary>
        /// Releases all resources used by the PackReader.
        /// </summary>
        void Dispose();
    }
}
```

## Contract Tests

### ReadAsync Tests
- **MUST** correctly read commit objects from pack files
- **MUST** correctly read tree objects from pack files
- **MUST** correctly read blob objects from pack files
- **MUST** correctly read tag objects from pack files
- **MUST** correctly reconstruct offset delta objects
- **MUST** correctly reconstruct reference delta objects
- **MUST** handle nested delta chains up to maximum depth
- **MUST** throw ObjectDisposedException when disposed
- **MUST** throw NotImplementedException for unknown types
- **MUST** use dependent entry provider for delta reconstruction

### GetByOffsetAsync Tests
- **MUST** cache objects by offset for repeated access
- **MUST** return cached object on subsequent calls
- **MUST** call provider only once per offset
- **MUST** handle concurrent access to same offset

### ExtractOffset Tests
- **MUST** correctly decode single-byte offsets
- **MUST** correctly decode multi-byte offsets
- **MUST** handle maximum offset values
- **MUST** throw EndOfStreamException on incomplete data

### Disposal Tests
- **MUST** release file resources on disposal
- **MUST** clear internal caches on disposal
- **MUST** prevent further operations after disposal