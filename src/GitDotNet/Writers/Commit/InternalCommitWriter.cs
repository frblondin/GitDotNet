using System.Diagnostics.CodeAnalysis;
using System.Text;
using GitDotNet.Writers;

namespace GitDotNet;

internal abstract class InternalCommitWriter(TransformationComposer composer)
{
    internal abstract Task<HashId> WriteAsync(TreeEntry? baseRootTree, CommitEntry commit);

    protected static async Task<HashId> BuildTreeHierarchyAsync(Dictionary<GitPath, HashId> modifiedBlobs,
        Func<GitPath, Task<HashId>> processDirectoryFunc)
    {
        // Sort paths by depth (deepest first) to build from leaves up
        var sortedPaths = modifiedBlobs.Keys
            .SelectMany(GetAllParentPaths)
            .Distinct()
            .OrderByDescending(p => p.Length);

        // Process each directory from deepest to root
        foreach (var path in sortedPaths)
        {
            await processDirectoryFunc(path).ConfigureAwait(false);
        }

        // Finally, process the root tree
        return await processDirectoryFunc(GitPath.Empty).ConfigureAwait(false);
    }

    private static IEnumerable<GitPath> GetAllParentPaths(GitPath path)
    {
        var current = path.Parent;
        while (current is not null)
        {
            yield return current;
            if (current.IsEmpty)
                yield break;
            current = current.Parent;
        }
    }

    protected async Task<HashId> ProcessDirectoryAsync(TreeEntry? baseRootTree, GitPath path,
        Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<GitPath, HashId> modifiedTrees,
        IObjectResolver objectResolver, Func<HashId, byte[], Task> writeTreeFunc)
    {
        var targetTree = await GetOrCreateTreeEntryAsync(baseRootTree, path, objectResolver).ConfigureAwait(false);

        // Build new children list
        var newChildren = new Dictionary<GitPath, TreeEntryItem>();

        AddUnmodifiedChildren(path, modifiedBlobs, modifiedTrees, objectResolver, targetTree, newChildren);
        AddUntrackedChildren(path, modifiedBlobs, objectResolver, newChildren);
        AddDirectTrees(path, modifiedTrees, objectResolver, newChildren);

        // Sort children by name (Git requirement for deterministic tree hashes)
        var sorted = newChildren.Values.OrderBy(x => x.Name, StringComparer.Ordinal);

        // Create tree content in Git format
        var treeContent = TreeEntryWriter.Write(sorted);
        var treeId = HashId.Create(EntryType.Tree, treeContent);

        // Only write tree if it's different from the original tree
        var shouldWriteTree = path.IsEmpty || !targetTree.Id.Equals(treeId);

        if (shouldWriteTree)
        {
            await writeTreeFunc(treeId, treeContent).ConfigureAwait(false);
        }

        // Store in modified trees for parent processing
        if (!path.IsEmpty)
        {
            modifiedTrees[path] = treeId;
        }

        return treeId;
    }

    private static async Task<TreeEntry> GetOrCreateTreeEntryAsync(TreeEntry? baseRootTree, GitPath dirPath, IObjectResolver objectResolver)
    {
        // Get the tree at this path
        var targetTree = default(TreeEntry);
        if (dirPath.IsEmpty && baseRootTree is not null)
        {
            targetTree = baseRootTree;
        }
        else if (baseRootTree is not null)
        {
            var treeItem = await baseRootTree.GetFromPathAsync(dirPath).ConfigureAwait(false);
            if (treeItem is not null)
            {
                targetTree = await treeItem.GetEntryAsync<TreeEntry>().ConfigureAwait(false);
            }
        }
        // Create new directory tree (empty tree for new directories)
        targetTree ??= new TreeEntry(HashId.Empty, [], objectResolver);
        return targetTree;
    }

    private void AddUnmodifiedChildren(GitPath path, Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<GitPath, HashId> modifiedTrees, IObjectResolver objectResolver, TreeEntry targetTree, Dictionary<GitPath, TreeEntryItem> newChildren)
    {
        // Add existing children that are not modified
        foreach (var child in targetTree.Children)
        {
            var childPath = path.AddChild(child.Name);
            bool isModified = false;

            // Check if this child is directly modified (blob change)
            if (modifiedBlobs.TryGetValue(childPath, out var newBlobId))
            {
                AddModifiedBlobChild(objectResolver, newChildren, child, childPath, newBlobId);
                isModified = true;
                // If removed (newBlobId.IsNull), don't add to new children
            }
            else if (child.Mode.EntryType == EntryType.Tree)
            {
                isModified = AddModifiedSubtreeChild(modifiedTrees, objectResolver, newChildren, childPath, child, isModified);
            }

            // If not modified, keep the existing child
            if (!isModified)
            {
                newChildren[childPath] = child;
            }
        }
    }

    private void AddModifiedBlobChild(IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren, TreeEntryItem child, GitPath childPath, HashId newBlobId)
    {
        if (!newBlobId.IsNull) // Not removed
        {
            var fileMode = composer.Changes.TryGetValue(childPath, out var change) && change.FileMode is not null ?
                change.FileMode :
                child.Mode;
            var newChild = new TreeEntryItem(fileMode, child.Name, newBlobId, objectResolver.GetAsync<Entry>);
            newChildren[childPath] = newChild;
        }
    }

    private static bool AddModifiedSubtreeChild(Dictionary<GitPath, HashId> modifiedTrees, IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren, GitPath path, TreeEntryItem child, bool isModified)
    {
        // Check if any subtree is modified
        if (modifiedTrees.TryGetValue(path, out var newTreeId))
        {
            isModified = true;
            var newChild = new TreeEntryItem(child.Mode, child.Name, newTreeId, objectResolver.GetAsync<Entry>);
            newChildren[path] = newChild;
        }

        return isModified;
    }

    private void AddUntrackedChildren(GitPath path, Dictionary<GitPath, HashId> modifiedBlobs, IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren)
    {
        // Add new files that don't exist in the original tree
        foreach (var (blobPath, id) in modifiedBlobs)
        {
            if (TryExtractDirectRelativePath(id, blobPath, path, newChildren, out var name))
            {
                var fileMode = composer.Changes.TryGetValue(blobPath, out var change) && change.FileMode is not null ?
                    change.FileMode :
                    FileMode.RegularFile;
                var newChild = new TreeEntryItem(fileMode, name, id, objectResolver.GetAsync<Entry>);
                newChildren[blobPath] = newChild;
            }
        }
    }

    private static void AddDirectTrees(GitPath path, Dictionary<GitPath, HashId> modifiedTrees, IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren)
    {
        // Add trees that don't exist in the original tree
        foreach (var (treePath, id) in modifiedTrees)
        {
            if (TryExtractDirectRelativePath(id, treePath, path, newChildren, out var name))
            {
                var newChild = new TreeEntryItem(new(ObjectType.Tree), name, id, objectResolver.GetAsync<Entry>);
                newChildren[treePath] = newChild;
            }
        }
    }

    private static bool TryExtractDirectRelativePath(HashId id, GitPath path, GitPath dirPath, Dictionary<GitPath, TreeEntryItem> newChildren, [NotNullWhen(true)] out string? name)
    {
        // Direct child and not already added
        if (!id.IsNull && path.Length - 1 == dirPath.Length && path.Parent == dirPath && !newChildren.ContainsKey(path))
        {
            name = path.Name;
            return true;
        }
        name = null;
        return false;
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
