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

        var anyItem = AddTrackedChildren(path, modifiedBlobs, modifiedTrees, objectResolver, targetTree, newChildren);
        anyItem |= AddUntrackedChildren(path, modifiedBlobs, objectResolver, newChildren);
        anyItem |= AddDirectTrees(path, modifiedTrees, objectResolver, newChildren);

        if (!anyItem)
        {
            // No changes in this directory and no children, so it should be removed
            return modifiedTrees[path] = HashId.Empty;
        }

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
        return modifiedTrees[path] = treeId;
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

    private bool AddTrackedChildren(GitPath path, Dictionary<GitPath, HashId> modifiedBlobs, Dictionary<GitPath, HashId> modifiedTrees, IObjectResolver objectResolver, TreeEntry targetTree, Dictionary<GitPath, TreeEntryItem> newChildren)
    {
        var anyItem = false;
        foreach (var child in targetTree.Children)
        {
            var childPath = path.AddChild(child.Name);

            // Check if this child is directly modified
            if (modifiedBlobs.TryGetValue(childPath, out var newBlobId))
            {
                anyItem |= AddModifiedBlobChild(objectResolver, newChildren, child, childPath, newBlobId);
            }
            else if (modifiedTrees.TryGetValue(childPath, out var newTreeId))
            {
                anyItem |= AddModifiedSubtreeChild(childPath, objectResolver, newChildren, child, newTreeId);
            }
            else
            {
                // If not modified, keep the existing child
                anyItem = true;
                newChildren[childPath] = child;
            }
        }
        return anyItem;
    }

    private bool AddModifiedBlobChild(IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren, TreeEntryItem child, GitPath childPath, HashId newBlobId)
    {
        if (newBlobId.IsNull)
        {
            // Removed
            return false;
        }
        var fileMode = composer.Changes.TryGetValue(childPath, out var change) && change.FileMode is not null ?
                change.FileMode :
                child.Mode;
        var newChild = new TreeEntryItem(fileMode, child.Name, newBlobId, objectResolver.GetAsync<Entry>);
        newChildren[childPath] = newChild;
        return true;
    }

    private static bool AddModifiedSubtreeChild(GitPath path, IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren, TreeEntryItem child, HashId newTreeId)
    {
        if (newTreeId.IsNull)
        {
            // Removed
            return false;
        }
        var newChild = new TreeEntryItem(child.Mode, child.Name, newTreeId, objectResolver.GetAsync<Entry>);
        newChildren[path] = newChild;
        return true;
    }

    private bool AddUntrackedChildren(GitPath path, Dictionary<GitPath, HashId> modifiedBlobs, IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren)
    {
        var anyItem = false;
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
                anyItem = true;
            }
        }
        return anyItem;
    }

    private static bool AddDirectTrees(GitPath path, Dictionary<GitPath, HashId> modifiedTrees, IObjectResolver objectResolver, Dictionary<GitPath, TreeEntryItem> newChildren)
    {
        var anyItem = false;
        foreach (var (treePath, id) in modifiedTrees)
        {
            if (id.IsNull) continue;
            // Add trees that don't exist in the original tree
            if (TryExtractDirectRelativePath(id, treePath, path, newChildren, out var name))
            {
                var newChild = new TreeEntryItem(new(ObjectType.Tree), name, id, objectResolver.GetAsync<Entry>);
                newChildren[treePath] = newChild;
                anyItem = true;
            }
        }
        return anyItem;
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
