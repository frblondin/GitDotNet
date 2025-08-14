using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using GitDotNet.Readers;

namespace GitDotNet;

/// <summary>Represents a Git tree entry, which contains references to other entries (blobs, trees, etc.).</summary>
public class TreeEntry : Entry
{
    private readonly IObjectResolver _objectResolver;
    private IList<TreeEntryItem>? _items;

    internal TreeEntry(HashId id, byte[] data, IObjectResolver objectResolver)
        : base(EntryType.Tree, id, data)
    {
        _objectResolver = objectResolver;
    }

    /// <summary>Gets the items contained in the tree entry.</summary>
    public IList<TreeEntryItem> Children => _items ??= TreeEntryReader.Parse(Data, _objectResolver);

    /// <summary>Gets the item with the specified relative path.</summary>
    /// <param name="path">The path of the item.</param>
    public async Task<TreeEntryItem?> GetFromPathAsync(GitPath path)
    {
        var child = Children.FirstOrDefault(x => x.Name.Equals(path.Root, StringComparison.Ordinal));
        if (child is not null && path.Length > 1)
        {
            return await child.GetRelativePathAsync(path.ChildPath).ConfigureAwait(false);
        }
        return child;
    }

    /// <summary>Tries to get the relative path to the specified entry.</summary>
    /// <param name="entry">The entry to get the relative path to.</param>
    /// <returns>The relative path to the entry, or <see langword="null"/> if the entry was not found.</returns>
    public async Task<GitPath> GetPathToAsync(Entry entry)
    {
        foreach (var child in Children)
        {
            var path = await child.TryGetRelativePathToAsync(entry).ConfigureAwait(false);
            if (path is not null)
                return path;
        }
        throw new KeyNotFoundException($"Entry '{entry.Id}' not found in commit tree.");
    }

    /// <summary>Gets all blob entries in the tree.</summary>
    /// <param name="basePath">This optional parameter specifies the base path for the blob entries being retrieved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An enumerable of all blob entries in the tree.</returns>
    public async IAsyncEnumerable<(GitPath Path, TreeEntryItem BlobEntry)> GetAllBlobEntriesAsync(GitPath? basePath = null, CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<(GitPath Path, TreeEntryItem Stream)>();
        var task = GetAllBlobEntriesAsync(channel, x => x, basePath, cancellationToken).ConfigureAwait(false);
        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }
        await task;
    }

    /// <summary>Gets all blob entries in the tree.</summary>
    /// <param name="channel">The channel to write the results to.</param>
    /// <param name="func">The function to apply to each blob entry.</param>
    /// <param name="basePath">This optional parameter specifies the base path for the blob entries being retrieved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An enumerable of all blob entries in the tree.</returns>
    [ExcludeFromCodeCoverage]
    public async Task GetAllBlobEntriesAsync<TResult>(Channel<TResult> channel, Func<(GitPath Path, TreeEntryItem BlobEntry), TResult> func, GitPath? basePath = null, CancellationToken cancellationToken = default)
    {
        var path = new List<string>(basePath ?? []);
        foreach (var child in Children)
        {
            path.Add(child.Name);
            await child.GetAllBlobEntriesAsync(channel, func, path, cancellationToken).ConfigureAwait(false);
            path.RemoveAt(path.Count - 1);
        }
        channel.Writer.Complete();
    }
}

