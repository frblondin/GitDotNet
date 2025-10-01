# Quickstart: Git Object Resolution System

## Overview 
This quickstart guide demonstrates how to use the Git Object Resolution System to read Git objects from a repository. The system provides a unified interface for accessing commits, trees, blobs, and tags whether they're stored as loose objects or in pack files.

## Prerequisites
- .NET 8 or later
- GitDotNet library reference
- A Git repository with objects to read

## Basic Usage

### Setting Up the Object Resolver

```csharp
using GitDotNet;
using GitDotNet.Readers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Set up dependency injection
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.AddMemoryCache();
services.AddGitDotNet(); // Extension method that registers all required services

var serviceProvider = services.BuildServiceProvider();

// Create object resolver factory
var objectResolverFactory = serviceProvider.GetRequiredService<ObjectResolverFactory>();

// Create resolver for a specific repository
using var objectResolver = objectResolverFactory("/path/to/repository", useReadCommitGraph: true);
```

### Reading Git Objects

#### Reading a Commit
```csharp
// Get commit by full hash
var commitHash = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
var commit = await objectResolver.GetAsync<CommitEntry>(commitHash);

Console.WriteLine($"Commit: {commit.Id}");
Console.WriteLine($"Author: {commit.Author.Name} <{commit.Author.Email}>");
Console.WriteLine($"Message: {commit.Message}");
Console.WriteLine($"Tree: {commit.RootTree}");
Console.WriteLine($"Parents: {string.Join(", ", commit.ParentIds)}");
```

#### Reading a Tree (Directory Structure)
```csharp
// Get tree from commit
var tree = await objectResolver.GetAsync<TreeEntry>(commit.RootTree);

Console.WriteLine($"Tree entries ({tree.Entries.Count}):");
foreach (var entry in tree.Entries)
{
    Console.WriteLine($"  {entry.Mode:D6} {entry.Name} {entry.Hash}");
}
```

#### Reading a Blob (File Content)
```csharp
// Find a blob in the tree
var fileEntry = tree.Entries.FirstOrDefault(e => e.Name == "README.md");
if (fileEntry != null)
{
    var blob = await objectResolver.GetAsync<BlobEntry>(fileEntry.Hash);
    var content = System.Text.Encoding.UTF8.GetString(blob.Content);
    Console.WriteLine($"File content:\n{content}");
}
```

#### Reading a Tag
```csharp
// Get tag by hash
var tagHash = new HashId("t4g5h6i7j8k9012345678901234567890123abcd");
var tag = await objectResolver.GetAsync<TagEntry>(tagHash);

Console.WriteLine($"Tag: {tag.Id}");
Console.WriteLine($"Target: {tag.Target} ({tag.TargetType})");
Console.WriteLine($"Tagger: {tag.Tagger.Name} <{tag.Tagger.Email}>");
Console.WriteLine($"Message: {tag.Message}");
```

### Partial Hash Lookup

```csharp
// Use partial hash (minimum 4 characters)
var partialHash = new HashId("a1b2c3d4");
try 
{
    var commit = await objectResolver.GetAsync<CommitEntry>(partialHash);
    Console.WriteLine($"Found commit: {commit.Id}");
}
catch (AmbiguousHashException)
{
    Console.WriteLine("Multiple objects match the partial hash");
}
```

### Safe Object Retrieval

```csharp
// Use TryGetAsync to avoid exceptions for missing objects
var maybeCommit = await objectResolver.TryGetAsync<CommitEntry>(someHash);
if (maybeCommit != null)
{
    Console.WriteLine($"Found commit: {maybeCommit.Id}");
}
else
{
    Console.WriteLine("Commit not found");
}
```

### Working with Log Entries (Optimized for Commit History)

```csharp
// Get optimized log entry for commit history operations
var logEntry = await objectResolver.GetAsync<LogEntry>(commitHash);

Console.WriteLine($"Commit: {logEntry.Id}");
Console.WriteLine($"Timestamp: {logEntry.Timestamp}");
Console.WriteLine($"Tree: {logEntry.RootTree}");
Console.WriteLine($"Parents: {string.Join(", ", logEntry.ParentIds)}");

// LogEntry is lighter than CommitEntry for history traversal
```

## Advanced Usage

### Repository Walking
```csharp
async Task WalkCommitHistory(HashId startCommit, int maxDepth = 100) 
{
    var visited = new HashSet<HashId>();
    var queue = new Queue<(HashId hash, int depth)>();
    queue.Enqueue((startCommit, 0));

    while (queue.Count > 0 && queue.Peek().depth < maxDepth)
    {
        var (hash, depth) = queue.Dequeue();
        
        if (visited.Contains(hash))
            continue;
        visited.Add(hash);

        var commit = await objectResolver.GetAsync<LogEntry>(hash);
        Console.WriteLine($"{"".PadLeft(depth * 2)}{commit.Id} ({commit.Timestamp:yyyy-MM-dd})");

        foreach (var parent in commit.ParentIds)
        {
            queue.Enqueue((parent, depth + 1));
        }
    }
}

// Start walking from HEAD commit
await WalkCommitHistory(headCommitHash, maxDepth: 50);
```

### Directory Tree Traversal
```csharp
async Task PrintDirectoryTree(HashId treeHash, string path = "", int indent = 0)
{
    var tree = await objectResolver.GetAsync<TreeEntry>(treeHash);
    
    foreach (var entry in tree.Entries.OrderBy(e => e.Name))
    {
        var indentStr = new string(' ', indent * 2);
        Console.WriteLine($"{indentStr}{entry.Name}");
        
        // Recursively process subdirectories
        if (entry.Mode == FileMode.Tree)
        {
            await PrintDirectoryTree(entry.Hash, $"{path}/{entry.Name}", indent + 1);
        }
        else if (entry.Mode == FileMode.Blob)
        {
            // Could read blob content here if needed
            // var blob = await objectResolver.GetAsync<BlobEntry>(entry.Hash);
        }
    }
}

// Print the entire directory structure
await PrintDirectoryTree(commit.RootTree);
```

### Performance Considerations

```csharp
// The resolver automatically caches objects in memory
// Objects accessed multiple times will be served from cache

// For memory-sensitive applications, you can configure cache limits
var options = new GitConnection.Options
{
    CacheMaxMemory = 100 * 1024 * 1024, // 100 MB cache limit
    CacheExpiration = TimeSpan.FromMinutes(30)
};

// Objects are streamed for memory efficiency with large blobs
var largeBlob = await objectResolver.GetAsync<BlobEntry>(largeBlobHash);
// The content is loaded on-demand, not when the BlobEntry is created
```

## Error Handling

```csharp
try 
{
    var obj = await objectResolver.GetAsync<CommitEntry>(hash);
}
catch (KeyNotFoundException ex)
{
    Console.WriteLine($"Object not found: {ex.Message}");
}
catch (AmbiguousHashException ex)
{
    Console.WriteLine($"Ambiguous hash: {ex.Message}");
}
catch (ObjectDisposedException ex)
{
    Console.WriteLine($"Resolver disposed: {ex.Message}");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Corrupted data: {ex.Message}");
}
```

## Resource Management

```csharp
// Always dispose the object resolver to free resources
using var objectResolver = objectResolverFactory(repositoryPath, useReadCommitGraph: true);

// Or use explicit disposal
var objectResolver = objectResolverFactory(repositoryPath, useReadCommitGraph: true);
try 
{
    // Use the resolver...
}
finally 
{
    objectResolver.Dispose();
}
```

## Integration with Dependency Injection

```csharp
// Register in ConfigureServices
services.AddSingleton<ObjectResolverFactory>(serviceProvider =>
{
    // Configure factory with required dependencies
    return (repositoryPath, useReadCommitGraph) => 
        new ObjectResolver(
            repositoryPath, 
            useReadCommitGraph,
            serviceProvider.GetRequiredService<IOptions<GitConnection.Options>>(),
            serviceProvider.GetRequiredService<PackManagerFactory>(),
            serviceProvider.GetRequiredService<LooseReaderFactory>(),
            serviceProvider.GetRequiredService<LfsReaderFactory>(),
            serviceProvider.GetRequiredService<CommitGraphReaderFactory>(),
            serviceProvider.GetRequiredService<IMemoryCache>(),
            serviceProvider.GetRequiredService<IFileSystem>(),
            serviceProvider.GetRequiredService<ILogger<ObjectResolver>>()
        );
});

// Use in your services
public class GitService
{
    private readonly ObjectResolverFactory _objectResolverFactory;
    
    public GitService(ObjectResolverFactory objectResolverFactory)
    {
        _objectResolverFactory = objectResolverFactory;
    }
    
    public async Task<string> GetCommitMessage(string repositoryPath, HashId commitHash)
    {
        using var resolver = _objectResolverFactory(repositoryPath, useReadCommitGraph: false);
        var commit = await resolver.GetAsync<CommitEntry>(commitHash);
        return commit.Message;
    }
}
```

This quickstart covers the essential operations for reading Git objects. The system handles all the complexity of Git's storage formats while providing a clean, type-safe API for your applications.