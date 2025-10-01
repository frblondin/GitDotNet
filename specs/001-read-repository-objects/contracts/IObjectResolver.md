# IObjectResolver Contract

```csharp
using System;
using System.Threading.Tasks;

namespace GitDotNet
{
    /// <summary>
    /// Represents a collection of Git objects in a repository.
    /// </summary>
    public interface IObjectResolver : IDisposable
    {
        /// <summary>
        /// Retrieves a Git object by its hash.
        /// </summary>
        /// <typeparam name="TEntry">The type of Git object entry to retrieve.</typeparam>
        /// <param name="id">The hash of the Git object.</param>
        /// <returns>The Git object associated with the specified hash.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the object hash is not found.</exception>
        /// <exception cref="AmbiguousHashException">Thrown when partial hash matches multiple objects.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the resolver has been disposed.</exception>
        Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry;

        /// <summary>
        /// Attempts to retrieve a Git object by its hash.
        /// </summary>
        /// <typeparam name="TEntry">The type of Git object entry to retrieve.</typeparam>
        /// <param name="id">The hash of the Git object.</param>
        /// <returns>The Git object associated with the specified hash, or null if not found.</returns>
        /// <exception cref="AmbiguousHashException">Thrown when partial hash matches multiple objects.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the resolver has been disposed.</exception>
        Task<TEntry?> TryGetAsync<TEntry>(HashId id) where TEntry : Entry;
    }
}
```

## Contract Tests

### GetAsync Tests
- **MUST** return correct object for valid full hash
- **MUST** return correct object for valid partial hash (4+ characters)
- **MUST** throw KeyNotFoundException for non-existent hash
- **MUST** throw AmbiguousHashException for ambiguous partial hash
- **MUST** throw ObjectDisposedException when resolver is disposed
- **MUST** handle all Entry types (CommitEntry, TreeEntry, BlobEntry, TagEntry, LogEntry)

### TryGetAsync Tests
- **MUST** return correct object for valid full hash
- **MUST** return correct object for valid partial hash (4+ characters)
- **MUST** return null for non-existent hash
- **MUST** throw AmbiguousHashException for ambiguous partial hash
- **MUST** throw ObjectDisposedException when resolver is disposed
- **MUST** handle all Entry types (CommitEntry, TreeEntry, BlobEntry, TagEntry, LogEntry)