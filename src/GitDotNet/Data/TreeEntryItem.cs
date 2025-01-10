using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace GitDotNet;

/// <summary>Represents an item in a Git tree entry.</summary>
[DebuggerDisplay("{Name,nq}")]
public record class TreeEntryItem
{
    private readonly Func<Task<Entry>> _entryProvider;
    private Entry? _entry;

    internal TreeEntryItem(FileMode mode, string name, HashId id, Func<Task<Entry>> entryProvider)
    {
        Mode = mode;
        Name = name;
        Id = id;
        _entryProvider = entryProvider;
    }

    /// <summary>Gets the file mode of the tree entry item.</summary>
    public FileMode Mode { get; }

    /// <summary>Gets the name of the tree entry item.</summary>
    public string Name { get; }

    /// <summary>Gets the hash of the tree entry item.</summary>
    public HashId Id { get; }

    /// <summary>Gets the entry associated with the tree entry item.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Entry Entry => AsyncHelper.RunSync(GetEntryAsync<Entry>);

    /// <summary>Asynchronously gets the entry associated with the tree entry item.</summary>
    /// <returns>The entry associated with the tree entry item.</returns>
    public async Task<TEntry> GetEntryAsync<TEntry>() where TEntry : Entry =>
        (_entry ??= await _entryProvider()) as TEntry ??
        throw new InvalidOperationException($"Expected a {typeof(TEntry).Name} entry.");

    /// <summary>Gets the item with the specified relative path.</summary>
    /// <param name="relativePath">The path of the item relatively to current tree.</param>
    /// <returns>The item with the specified relative path.</returns>
    public async Task<TreeEntryItem?> GetRelativePathAsync(GitPath relativePath)
    {
        var currentPath = relativePath;
        var currentItem = this;

        while (true)
        {
            var tree = await currentItem.GetEntryAsync<TreeEntry>();
            currentItem = tree.Children.FirstOrDefault(
                x => x.Name.Equals(currentPath.Root, StringComparison.Ordinal));

            if (currentItem is null) return null;
            if (currentPath.Length == 1) return currentItem;

            currentPath = currentPath.ChildPath;
        }
    }

    /// <summary>Tries to get the relative path to the specified entry.</summary>
    /// <param name="entry">The entry to get the relative path to.</param>
    /// <returns>The relative path to the entry, or <see langword="null"/> if the entry was not found.</returns>
    public async Task<GitPath?> TryGetRelativePathToAsync(Entry entry)
    {
        return await TryGetRelativePathToAsync(entry, new Stack<string>([Name]));
    }

    private async Task<GitPath?> TryGetRelativePathToAsync(Entry entry, Stack<string> path)
    {
        if (Id.Equals(entry.Id)) return new GitPath([.. path.Reverse()]);

        if (Mode.EntryType == EntryType.Tree)
        {
            var item = await GetEntryAsync<TreeEntry>();
            foreach (var child in item.Children)
            {
                path.Push(child.Name);
                var result = await child.TryGetRelativePathToAsync(entry, path);
                if (result != null)
                    return result;
                path.Pop();
            }
        }

        return null;
    }

    internal async Task GetAllBlobEntriesAsync(List<(GitPath Path, TreeEntryItem BlobEntry)> result, Stack<string> path)
    {
        if (Mode.EntryType == EntryType.Blob) result.Add((path.Reverse().ToArray(), this));

        if (Mode.EntryType == EntryType.Tree)
        {
            var item = await GetEntryAsync<TreeEntry>();
            foreach (var child in item.Children)
            {
                path.Push(child.Name);
                await child.GetAllBlobEntriesAsync(result, path);
                path.Pop();
            }
        }
    }

    internal async Task GetAllBlobEntriesAsync<TResult>(Channel<TResult> channel, Func<(GitPath Path, TreeEntryItem BlobEntry), TResult> func, Stack<string> path)
    {
        if (Mode.EntryType == EntryType.Blob)
        {
            var result = func((path.Reverse().ToArray(), this));
            await channel.Writer.WriteAsync(result);
        }

        if (Mode.EntryType == EntryType.Tree)
        {
            var item = await GetEntryAsync<TreeEntry>();
            foreach (var child in item.Children)
            {
                path.Push(child.Name);
                await child.GetAllBlobEntriesAsync(channel, func, path);
                path.Pop();
            }
        }
    }
}