using System.IO.Abstractions;
using GitDotNet.Writers;
using static GitDotNet.TransformationComposer;

namespace GitDotNet;

internal class PackCommitWriter(TransformationComposer composer, IRepositoryInfo info, PackWriterFactory packWriterFactory, IFileSystem fileSystem)
#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    : InternalCommitWriter(composer)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
{
    private const int MaxPackBlobSize = 512_000_000;

    internal override async Task<HashId> WriteAsync(TreeEntry? baseRootTree, CommitEntry commit)
    {
        using var packWriter = packWriterFactory(info);
        var modifiedBlobs = new Dictionary<GitPath, HashId>();
        var modifiedTrees = new Dictionary<string, HashId>();
        var addedObjects = new HashSet<HashId>(); // Track added objects to avoid duplicates
        var objectResolver = commit.ObjectResolver;

        // Step 1: Process all blob changes and add them to the pack
        await ProcessBlobChangesAsync(packWriter, modifiedBlobs, addedObjects).ConfigureAwait(false);

        // Step 2: Build the new tree hierarchy from bottom up using shared method
        var newRootTreeId = await BuildTreeHierarchySharedAsync(
            baseRootTree, modifiedBlobs, modifiedTrees, objectResolver,
            async (baseRoot, dirPath, blobs, trees, resolver) =>
                await ProcessDirectorySharedAsync(baseRoot, dirPath, blobs, trees, resolver, (treeId, treeContent) =>
                    {
                        if (packWriter.TryAddEntry(EntryType.Tree, treeId, treeContent))
                        {
                            addedObjects.Add(treeId);
                        }
                        return Task.FromResult(true);
                    }).ConfigureAwait(false)
        ).ConfigureAwait(false);

        // Step 3: Create the new commit with the new root tree
        var result = CreateNewCommit(packWriter, commit, newRootTreeId, addedObjects);

        // Step 4: Build entry paths mapping for enhanced delta optimization
        var entryPaths = PackCommitWriter.BuildEntryPathsMapping(modifiedBlobs, modifiedTrees);

        // Step 5: Write the pack file with enhanced delta compression using previous tree context
        await packWriter.WritePackAsync(baseRootTree, entryPaths).ConfigureAwait(false);

        return result;
    }

    /// <summary>Builds a mapping from entry IDs to their paths for enhanced delta optimization.</summary>
    /// <param name="modifiedBlobs">Dictionary of modified blob paths and their IDs.</param>
    /// <param name="modifiedTrees">Dictionary of modified tree paths and their IDs.</param>
    /// <returns>A dictionary mapping entry IDs to their paths.</returns>
    private static Dictionary<HashId, GitPath> BuildEntryPathsMapping(Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<string, HashId> modifiedTrees)
    {
        var entryPaths = new Dictionary<HashId, GitPath>();

        // Map blob entries
        foreach (var (path, hashId) in modifiedBlobs)
        {
            if (!hashId.IsNull) // Skip removed entries
            {
                entryPaths[hashId] = path;
            }
        }

        // Map tree entries
        foreach (var (pathString, hashId) in modifiedTrees)
        {
            var path = new GitPath(pathString);
            entryPaths[hashId] = path;
        }

        return entryPaths;
    }

    private async Task ProcessBlobChangesAsync(PackWriter packWriter, Dictionary<GitPath, HashId> modifiedBlobs, HashSet<HashId> addedObjects)
    {
        var looseWriter = new Lazy<LooseWriter>(() => new(info.Path, fileSystem));
        foreach (var (path, (changeType, stream, _)) in composer.Changes)
        {
            switch (changeType)
            {
                case TransformationType.AddOrModified when stream != null:
                {
                    // Read the blob data
                    stream.Position = 0;
                    using var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream).ConfigureAwait(false);
                    var blobData = memoryStream.ToArray();

                    if (stream.Length > MaxPackBlobSize)
                    {
                        // For large files, use loose writer to avoid memory issues
                        var looseBlobId = await looseWriter.Value.WriteObjectAsync(EntryType.Blob, blobData).ConfigureAwait(false);
                        modifiedBlobs[path] = looseBlobId;
                        addedObjects.Add(looseBlobId);
                        continue;
                    }

                    // Compute blob hash using Git's object format (header + content)
                    // The hash is computed on the full Git object format: "blob <size>\0<content>"
                    var blobId = HashId.Create(EntryType.Blob, blobData);

                    // Add only the raw blob data to pack (without the Git object header)
                    // Pack files store raw content, not the full Git object format
                    if (packWriter.TryAddEntry(EntryType.Blob, blobId, blobData))
                    {
                        addedObjects.Add(blobId);
                    }

                    modifiedBlobs[path] = blobId;
                    break;
                }
                case TransformationType.Removed:
                    // Mark for removal (will be excluded from new tree)
                    modifiedBlobs[path] = HashId.Empty;
                    break;
            }
        }
    }

    private static HashId CreateNewCommit(PackWriter packWriter, CommitEntry commit, HashId newTreeId, HashSet<HashId> addedObjects)
    {
        var commitContent = CreateCommitContent(commit, newTreeId);
        var commitId = HashId.Create(EntryType.Commit, commitContent);

        // Use TryAddEntry to avoid duplicates at the PackWriter level
        if (packWriter.TryAddEntry(EntryType.Commit, commitId, commitContent))
        {
            addedObjects.Add(commitId);
        }

        return commitId;
    }
}
