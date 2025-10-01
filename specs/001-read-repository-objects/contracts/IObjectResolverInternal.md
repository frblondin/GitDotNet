# ObjectResolver Internal Contract

```csharp
using System.Threading.Tasks;

namespace GitDotNet
{
    /// <summary>
    /// Internal interface for advanced object resolution operations.
    /// </summary>
    internal interface IObjectResolverInternal
    {
        /// <summary>
        /// Gets the pack manager for accessing pack file indices.
        /// </summary>
        IPackManager PackManager { get; }

        /// <summary>
        /// Retrieves raw object data by hash.
        /// </summary>
        /// <param name="id">The hash of the Git object.</param>
        /// <returns>Raw object data as byte array.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the object hash is not found.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the resolver has been disposed.</exception>
        Task<byte[]> GetDataAsync(HashId id);

        /// <summary>
        /// Retrieves a Git object by its hash.
        /// </summary>
        /// <typeparam name="TEntry">The type of Git object entry to retrieve.</typeparam>
        /// <param name="id">The hash of the Git object.</param>
        /// <returns>The Git object associated with the specified hash.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the object hash is not found.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the resolver has been disposed.</exception>
        Task<TEntry> GetAsync<TEntry>(HashId id) where TEntry : Entry;
    }
}
```

## Contract Tests

### GetDataAsync Tests
- **MUST** return correct raw data for valid hash
- **MUST** throw KeyNotFoundException for non-existent hash
- **MUST** throw ObjectDisposedException when resolver is disposed
- **MUST** handle both loose objects and packed objects

### PackManager Property Tests
- **MUST** return non-null IPackManager instance
- **MUST** provide access to pack file indices
- **MUST** remain consistent throughout object lifetime