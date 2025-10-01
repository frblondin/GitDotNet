# Data Model: Git Object Resolution System

## Core Entities

### HashId
**Purpose**: Immutable identifier for Git objects supporting both SHA-1 and SHA-256
**Fields**:
- `Hash`: ReadOnlyMemory<byte> - The hash bytes
- `IsEmpty`: bool - Whether this represents an empty/null hash
**Relationships**: Used as identifier in all Entry types
**Validation Rules**:
- Hash length must be 20 bytes (SHA-1) or 32 bytes (SHA-256)
- Hash bytes are immutable once created
**State Transitions**: None (immutable)

### UnlinkedEntry
**Purpose**: Raw Git object data before conversion to specific entry types
**Fields**:
- `Type`: EntryType - Object type (Commit, Tree, Blob, Tag)
- `Id`: HashId - Object identifier
- `Data`: byte[] - Raw object content
**Relationships**: Converted to specific Entry types by ObjectResolver
**Validation Rules**:
- Type must be valid EntryType enum value
- Data must not be null or empty
- Id must be valid HashId
**State Transitions**: One-way conversion to Entry subtypes

### Entry (Abstract Base)
**Purpose**: Base class for all Git object entries
**Fields**:
- `Id`: HashId - Object identifier
- `Data`: byte[] - Raw object content
- `Type`: EntryType - Object type
**Relationships**: Base class for CommitEntry, TreeEntry, BlobEntry, TagEntry, LogEntry
**Validation Rules**:
- Id must be valid HashId
- Data must not be null
**State Transitions**: Immutable once created

### CommitEntry : Entry
**Purpose**: Represents a Git commit object
**Fields**:
- `Author`: Signature - Commit author information
- `Committer`: Signature - Commit committer information
- `Message`: string - Commit message
- `RootTree`: HashId - Root tree hash
- `ParentIds`: IReadOnlyList<HashId> - Parent commit hashes
**Relationships**: References TreeEntry (RootTree), references other CommitEntry instances (ParentIds)
**Validation Rules**:
- RootTree must be valid HashId
- ParentIds must contain valid HashId instances
- Message must not be null
**State Transitions**: None (immutable)

### TreeEntry : Entry
**Purpose**: Represents a Git tree object (directory structure)
**Fields**:
- `Entries`: IReadOnlyList<TreeItem> - Tree items (files and subdirectories)
**Relationships**: Contains references to BlobEntry and other TreeEntry instances
**Validation Rules**:
- Entries must not be null
- All TreeItem instances must be valid
**State Transitions**: None (immutable)

### BlobEntry : Entry
**Purpose**: Represents a Git blob object (file content)
**Fields**:
- `Content`: byte[] - File content data
- `IsLfs`: bool - Whether this is an LFS pointer
- `LfsData`: byte[] - Actual LFS content if applicable
**Relationships**: May reference LFS objects
**Validation Rules**:
- Content must not be null
- If IsLfs is true, LfsData should contain actual content
**State Transitions**: LFS content may be lazy-loaded

### TagEntry : Entry
**Purpose**: Represents a Git tag object
**Fields**:
- `Target`: HashId - Tagged object hash
- `TargetType`: EntryType - Type of tagged object
- `Tagger`: Signature - Tag creator information
- `Message`: string - Tag message
**Relationships**: References any Entry type via Target
**Validation Rules**:
- Target must be valid HashId
- TargetType must be valid EntryType
- Message must not be null
**State Transitions**: None (immutable)

### LogEntry : Entry
**Purpose**: Optimized representation for commit log operations
**Fields**:
- `RootTree`: HashId - Root tree hash
- `ParentIds`: IReadOnlyList<HashId> - Parent commit hashes
- `Timestamp`: DateTimeOffset - Commit timestamp
**Relationships**: References TreeEntry (RootTree), references other LogEntry/CommitEntry instances (ParentIds)
**Validation Rules**:
- RootTree must be valid HashId
- ParentIds must contain valid HashId instances
- Timestamp must be valid DateTimeOffset
**State Transitions**: None (immutable)

## Supporting Types

### EntryType (Enum)
**Values**:
- `Commit = 1`
- `Tree = 2`
- `Blob = 3`
- `Tag = 4`
- `RefDelta = 7`
- `OfsDelta = 6`

### Signature
**Purpose**: Author/committer information
**Fields**:
- `Name`: string - Person name
- `Email`: string - Email address
- `Timestamp`: DateTimeOffset - When the signature was created
**Validation Rules**:
- Name and Email must not be null or empty
- Timestamp must be valid DateTimeOffset

### TreeItem
**Purpose**: Individual item within a tree entry
**Fields**:
- `Mode`: FileMode - File permissions and type
- `Name`: string - Item name
- `Hash`: HashId - Object hash
**Validation Rules**:
- Mode must be valid FileMode value
- Name must not be null or empty
- Hash must be valid HashId

### FileMode (Enum)
**Values**:
- `Tree = 040000` - Directory
- `Blob = 100644` - Regular file
- `BlobExecutable = 100755` - Executable file
- `Link = 120000` - Symbolic link
- `Commit = 160000` - Git submodule

## Reader Contracts

### IObjectResolver
**Purpose**: Primary interface for object resolution
**Methods**:
- `GetAsync<TEntry>(HashId id)`: Retrieve object by hash (throws if not found)
- `TryGetAsync<TEntry>(HashId id)`: Retrieve object by hash (returns null if not found)
**Constraints**:
- TEntry must inherit from Entry
- Methods must be async and support cancellation

### IObjectResolverInternal
**Purpose**: Internal interface for advanced operations
**Methods**:
- `GetDataAsync(HashId id)`: Get raw object data
- `GetAsync<TEntry>(HashId id)`: Get typed entry
**Properties**:
- `PackManager`: IPackManager - Access to pack file management

## Cache Strategy

### Cache Keys
- Primary cache key: `(HashId, EntryTypeName)`
- Separate cache entries for UnlinkedEntry vs specific Entry types
- LogEntry cached separately from CommitEntry for performance

### Cache Policies
- Configurable size limits via IOptions<IGitConnection.Options>
- LRU eviction strategy with priority-based eviction
- Memory pressure handling with automatic cleanup
- Disposal token integration for cleanup coordination

### Cache Lifecycle
- Objects cached on first access
- Cache entries expire based on configured policies
- Memory pressure triggers proactive eviction
- Disposal cancellation token invalidates all entries

## Error Handling

### Exception Types
- `KeyNotFoundException`: Object hash not found in repository
- `AmbiguousHashException`: Multiple objects match partial hash
- `ObjectDisposedException`: Accessing disposed resources
- `InvalidOperationException`: Corrupted data or invalid state
- `EndOfStreamException`: Unexpected end of data stream

### Error Context
- Hash information included in all exceptions
- Operation context provided where applicable
- Logging integration for troubleshooting
- Graceful degradation where possible

## Performance Characteristics

### Memory Usage
- Objects cached in memory with configurable limits
- Streaming operations for large objects
- Object pooling for frequently allocated types
- Lazy loading for expensive operations

### I/O Patterns
- Async operations throughout
- Minimal file handle usage
- Efficient pack file access patterns
- Stream-based processing for memory efficiency

### Concurrency
- Thread-safe caching mechanisms
- Concurrent access to multiple pack files
- No blocking operations in critical paths
- Proper async/await usage throughout