using System.IO.Abstractions;
using GitDotNet.Writers;
using static GitDotNet.TransformationComposer;

namespace GitDotNet;

internal class LooseCommitWriter(TransformationComposer composer, IRepositoryInfo info, IFileSystem fileSystem)
#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    : InternalCommitWriter(composer)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
{
    internal override async Task<HashId> WriteAsync(TreeEntry? baseRootTree, CommitEntry commit)
    {
        using var looseWriter = new LooseWriter(info.Path, fileSystem);
        var modifiedBlobs = new Dictionary<GitPath, HashId>();
        var modifiedTrees = new Dictionary<string, HashId>();
        var objectResolver = commit.ObjectResolver;

        // Step 1: Process all blob changes and write them as loose objects
        await ProcessBlobChangesForLooseAsync(looseWriter, modifiedBlobs).ConfigureAwait(false);

        // Step 2: Build the new tree hierarchy from bottom up using shared method
        var newRootTreeId = await BuildTreeHierarchySharedAsync(
            baseRootTree, modifiedBlobs, modifiedTrees, objectResolver,
            async (baseRoot, dirPath, blobs, trees, resolver) =>
                await ProcessDirectorySharedAsync(baseRoot, dirPath, blobs, trees, resolver,
                    async (treeId, treeContent) =>
                    {
                        await looseWriter.WriteObjectAsync(EntryType.Tree, treeContent).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false)
        ).ConfigureAwait(false);

        // Step 3: Create and write the new commit as a loose object
        var result = await LooseCommitWriter.CreateNewCommitForLooseAsync(looseWriter, commit, newRootTreeId).ConfigureAwait(false);

        return result;
    }

    private async Task ProcessBlobChangesForLooseAsync(LooseWriter looseWriter, Dictionary<GitPath, HashId> modifiedBlobs)
    {
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

                    // Write the blob as a loose object
                    var blobId = await looseWriter.WriteObjectAsync(EntryType.Blob, blobData).ConfigureAwait(false);
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

    private static async Task<HashId> CreateNewCommitForLooseAsync(LooseWriter looseWriter, CommitEntry commit, HashId newTreeId)
    {
        var commitContent = CreateCommitContent(commit, newTreeId);
        var commitId = await looseWriter.WriteObjectAsync(EntryType.Commit, commitContent).ConfigureAwait(false);
        return commitId;
    }
}
