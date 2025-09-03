using System.Text;

namespace GitDotNet;

internal abstract class InternalCommitWriter(TransformationComposer composer)
{
    internal abstract Task<HashId> WriteAsync(TreeEntry? baseRootTree, CommitEntry commit);

    protected static async Task<HashId> BuildTreeHierarchySharedAsync(TreeEntry? baseRootTree,
        Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<string, HashId> modifiedTrees,
        IObjectResolver objectResolver,
        Func<TreeEntry?, string, Dictionary<GitPath, HashId>, Dictionary<string, HashId>, IObjectResolver, Task<HashId>> processDirectoryFunc)
    {
        // Get all unique directory paths that need modification
        var affectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in modifiedBlobs.Keys)
        {
            for (int i = 0; i < path.Length - 1; i++)
            {
                var dirPath = string.Join("/", path.Take(i + 1));
                affectedPaths.Add(dirPath);
            }
        }

        // Sort paths by depth (deepest first) to build from leaves up
        var sortedPaths = affectedPaths.OrderByDescending(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries).Length).ToList();

        // Process each directory from deepest to root
        foreach (var dirPath in sortedPaths)
        {
            await processDirectoryFunc(baseRootTree, dirPath, modifiedBlobs, modifiedTrees, objectResolver).ConfigureAwait(false);
        }

        // Finally, process the root tree
        return await processDirectoryFunc(baseRootTree, "", modifiedBlobs, modifiedTrees, objectResolver).ConfigureAwait(false);
    }

    protected async Task<HashId> ProcessDirectorySharedAsync(TreeEntry? baseRootTree, string dirPath,
        Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<string, HashId> modifiedTrees,
        IObjectResolver objectResolver, Func<HashId, byte[], Task<bool>> writeTreeFunc)
    {
        var targetTree = await GetOrCreateTreeEntryAsync(baseRootTree, dirPath, objectResolver).ConfigureAwait(false);

        // Build new children list
        var newChildren = new List<TreeEntryItem>();
        var prefix = string.IsNullOrEmpty(dirPath) ? "" : dirPath + "/";

        AddUnmodifiedChildren(modifiedBlobs, modifiedTrees, objectResolver, targetTree, newChildren, prefix);
        AddUntrackedChildren(modifiedBlobs, objectResolver, newChildren, prefix);

        // Sort children by name (Git requirement for deterministic tree hashes)
        newChildren.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        // Create tree content in Git format
        var treeContent = CreateTreeContent(newChildren);
        var treeId = HashId.Create(EntryType.Tree, treeContent);

        // Only write tree if it's different from the original tree
        var shouldWriteTree = string.IsNullOrEmpty(dirPath) || !targetTree.Id.Equals(treeId);

        if (shouldWriteTree)
        {
            await writeTreeFunc(treeId, treeContent).ConfigureAwait(false);
        }

        // Store in modified trees for parent processing
        if (!string.IsNullOrEmpty(dirPath))
        {
            modifiedTrees[dirPath] = treeId;
        }

        return treeId;
    }

    private static async Task<TreeEntry> GetOrCreateTreeEntryAsync(TreeEntry? baseRootTree, string dirPath, IObjectResolver objectResolver)
    {
        // Get the tree at this path
        var targetTree = default(TreeEntry);
        if (string.IsNullOrEmpty(dirPath) && baseRootTree is not null)
        {
            targetTree = baseRootTree;
        }
        else if (baseRootTree is not null)
        {
            var treeItem = await baseRootTree.GetFromPathAsync(new GitPath(dirPath)).ConfigureAwait(false);
            if (treeItem is not null)
            {
                targetTree = await treeItem.GetEntryAsync<TreeEntry>().ConfigureAwait(false);
            }
        }
        // Create new directory tree (empty tree for new directories)
        targetTree ??= new TreeEntry(HashId.Empty, [], objectResolver);
        return targetTree;
    }

    private static byte[] CreateTreeContent(List<TreeEntryItem> children)
    {
        using var stream = new MemoryStream();

        foreach (var child in children)
        {
            // Write mode (octal string without leading zeros)
            var modeBytes = Encoding.ASCII.GetBytes(child.Mode.ToString());
            stream.Write(modeBytes);

            // Write space separator
            stream.WriteByte(0x20);

            // Write filename
            var nameBytes = Encoding.UTF8.GetBytes(child.Name);
            stream.Write(nameBytes);

            // Write null terminator
            stream.WriteByte(0x00);

            // Write 20-byte SHA-1 hash
            var hashBytes = child.Id.Hash.ToArray();
            stream.Write(hashBytes);
        }

        return stream.ToArray();
    }

    private void AddUnmodifiedChildren(Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<string, HashId> modifiedTrees, IObjectResolver objectResolver, TreeEntry targetTree, List<TreeEntryItem> newChildren, string prefix)
    {
        // Add existing children that are not modified
        foreach (var child in targetTree.Children)
        {
            var childPath = new GitPath(prefix + child.Name);
            bool isModified = false;

            // Check if this child is directly modified (blob change)
            if (modifiedBlobs.TryGetValue(childPath, out var newBlobId))
            {
                isModified = AddModifiedBlobChild(objectResolver, newChildren, child, childPath, newBlobId);
                // If removed (newBlobId.IsNull), don't add to new children
            }
            else if (child.Mode.EntryType == EntryType.Tree)
            {
                isModified = AddModifiedSubtreeChild(modifiedTrees, objectResolver, newChildren, prefix, child, isModified);
            }

            // If not modified, keep the existing child
            if (!isModified)
            {
                newChildren.Add(child);
            }
        }
    }

    private bool AddModifiedBlobChild(IObjectResolver objectResolver, List<TreeEntryItem> newChildren, TreeEntryItem child, GitPath childPath, HashId newBlobId)
    {
        var isModified = true;
        if (!newBlobId.IsNull) // Not removed
        {
            var fileMode = composer.Changes.TryGetValue(childPath, out var change) && change.FileMode is not null ?
                change.FileMode :
                child.Mode;
            var newChild = new TreeEntryItem(fileMode, child.Name, newBlobId, objectResolver.GetAsync<Entry>);
            newChildren.Add(newChild);
        }

        return isModified;
    }

    private static bool AddModifiedSubtreeChild(Dictionary<string, HashId> modifiedTrees, IObjectResolver objectResolver, List<TreeEntryItem> newChildren, string prefix, TreeEntryItem child, bool isModified)
    {
        // Check if any subtree is modified
        var childTreePath = prefix + child.Name;
        if (modifiedTrees.TryGetValue(childTreePath, out var newTreeId))
        {
            isModified = true;
            var newChild = new TreeEntryItem(child.Mode, child.Name, newTreeId, objectResolver.GetAsync<Entry>);
            newChildren.Add(newChild);
        }

        return isModified;
    }

    private void AddUntrackedChildren(Dictionary<GitPath, HashId> modifiedBlobs, IObjectResolver objectResolver, List<TreeEntryItem> newChildren, string prefix)
    {
        // Add new files that don't exist in the original tree
        foreach (var (path, blobId) in modifiedBlobs)
        {
            if (blobId.IsNull || !path.ToString().StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = path.ToString()[prefix.Length..];
            if (relativePath.Contains('/') || newChildren.Any(c => c.Name == relativePath)) // Direct child and not already added
            {
                continue;
            }

            var fileMode = composer.Changes.TryGetValue(path, out var change) && change.FileMode is not null ?
                change.FileMode :
                FileMode.RegularFile;
            var newChild = new TreeEntryItem(fileMode, relativePath, blobId, objectResolver.GetAsync<Entry>);
            newChildren.Add(newChild);
        }
    }

    protected static byte[] CreateCommitContent(CommitEntry commit, HashId newTreeId)
    {
        var content = new StringBuilder();

        // Tree reference
        content.Append($"tree {newTreeId}").Append('\n');

        // Parent references
        foreach (var parentId in commit.ParentIds)
        {
            content.Append($"parent {parentId}").Append('\n');
        }

        // Author information
        if (commit.Author != null)
        {
            var authorOffset = commit.Author.Timestamp.Offset;
            var offsetString = $"{(authorOffset.TotalMinutes >= 0 ? "+" : "-")}{Math.Abs(authorOffset.Hours):D2}{Math.Abs(authorOffset.Minutes % 60):D2}";
            content.Append($"author {commit.Author.Name} <{commit.Author.Email}> {commit.Author.Timestamp.ToUnixTimeSeconds()} {offsetString}").Append('\n');
        }

        // Committer information
        if (commit.Committer != null)
        {
            var committerOffset = commit.Committer.Timestamp.Offset;
            var offsetString = $"{(committerOffset.TotalMinutes >= 0 ? "+" : "-")}{Math.Abs(committerOffset.Hours):D2}{Math.Abs(committerOffset.Minutes % 60):D2}";
            content.Append($"committer {commit.Committer.Name} <{commit.Committer.Email}> {commit.Committer.Timestamp.ToUnixTimeSeconds()} {offsetString}").Append('\n');
        }

        // Empty line before message (Git format requirement)
        content.Append('\n');

        // Commit message
        content.Append(commit.Message);

        return Encoding.UTF8.GetBytes(content.ToString());
    }
}
