# LooseReader Contract

```csharp
using System;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers
{
    /// <summary>
    /// Factory delegate for creating LooseReader instances.
    /// </summary>
    /// <param name="path">Path to the objects directory.</param>
    /// <returns>A new LooseReader instance.</returns>
    internal delegate LooseReader LooseReaderFactory(string path);

    /// <summary>
    /// Reads individual Git object files from the objects directory.
    /// </summary>
    internal class LooseReader
    {
        /// <summary>
        /// Initializes a new instance of the LooseReader class.
        /// </summary>
        /// <param name="path">Path to the objects directory.</param>
        /// <param name="fileSystem">File system abstraction for testability.</param>
        /// <param name="logger">Optional logger instance.</param>
        LooseReader(string path, IFileSystem fileSystem, ILogger<LooseReader>? logger = null);

        /// <summary>
        /// Attempts to load a Git object by its hex string representation.
        /// </summary>
        /// <param name="hexString">Hex string representation of the object hash.</param>
        /// <returns>Tuple containing object type, data provider function, and length. Returns default if not found.</returns>
        /// <exception cref="AmbiguousHashException">Thrown when partial hash matches multiple objects.</exception>
        /// <exception cref="InvalidOperationException">Thrown when object data is corrupted.</exception>
        (EntryType Type, Func<Stream>? DataProvider, long Length) TryLoad(string hexString);
    }
}
```

## Contract Tests

### TryLoad Tests
- **MUST** return correct type, data provider, and length for valid full hash
- **MUST** return correct type, data provider, and length for valid partial hash (4+ characters)
- **MUST** return default values (default, null, -1) for non-existent hash
- **MUST** throw AmbiguousHashException when partial hash matches multiple objects
- **MUST** throw InvalidOperationException for corrupted object files
- **MUST** handle all object types (commit, tree, blob, tag)
- **MUST** correctly decompress zlib-compressed object files
- **MUST** correctly parse object headers (type and size)

### Data Provider Tests
- **MUST** return stream with correct object content when data provider is called
- **MUST** exclude object header from returned stream content
- **MUST** handle multiple calls to data provider function
- **MUST** properly dispose streams after use

### File System Integration Tests
- **MUST** use IFileSystem abstraction for all file operations
- **MUST** construct proper file paths from hex strings
- **MUST** handle missing directories gracefully
- **MUST** support both Windows and Unix path separators

### Object Parsing Tests
- **MUST** correctly identify commit object type from header
- **MUST** correctly identify tree object type from header
- **MUST** correctly identify blob object type from header
- **MUST** correctly identify tag object type from header
- **MUST** correctly parse object size from header
- **MUST** validate object header format