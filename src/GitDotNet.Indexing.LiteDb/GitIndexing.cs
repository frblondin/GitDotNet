using System.IO.Abstractions;
using System.Linq.Expressions;
using System.Reflection;
using GitDotNet.Indexing.LiteDb;
using GitDotNet.Indexing.LiteDb.Data;
using LangChain.DocumentLoaders;
using LangChain.Extensions;
using LangChain.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GitDotNet.Indexing.Realm;

/// <summary>Indexes the Git repository for searching.</summary>
public partial class GitIndexing : IDisposable
{
    private readonly GitConnection _connection;
    private readonly IOptions<Options> _options;
    private readonly IFileSystem _fileSystem;
    private readonly SqliteDatabaseContext _context;

    /// <summary>Initializes a new instance of the <see cref="GitIndexing"/> class.</summary>
    /// <param name="connection">The connection to the Git repository.</param>
    /// <param name="options">The options for the indexing.</param>
    /// <param name="fileSystem">The file system to use.</param>
    public GitIndexing(GitConnection connection, IOptions<Options> options, IFileSystem fileSystem)
    {
        _connection = connection;
        _options = options;
        _fileSystem = fileSystem;
        var path = _fileSystem.Path.Combine(_connection.Info.Path, "indexing.db");
        _context = new SqliteDatabaseContext(path, options);
        _context.Database.EnsureCreated();
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
    }

    /// <summary>Searches for blobs that match the specified predicate.</summary>
    /// <param name="commit">The commit to search in.</param>
    /// <param name="predicate">The predicate to match blobs against.</param>
    /// <returns>An asynchronous enumerable of blobs that match the predicate.</returns>
    public async IAsyncEnumerable<(string Path, BlobEntry Blob)> SearchAsync<TIndex>(CommitEntry commit, Expression<Func<TIndex, bool>> predicate)
        where TIndex : BlobIndex
    {
        var commitData = await EnsureIndexAsync(commit);
        var blobIds = commitData.Blobs.Values.ToList();
        var filteredBlobs = _context.Set<TIndex>().AsQueryable()
            .Where(x => blobIds.Contains(x.Id))
            .Where(predicate)
            .ToList();
        var blobPaths = commitData.Blobs.ToLookup(x => x.Value, x => x.Key);
        foreach (var data in filteredBlobs)
        {
            var paths = blobPaths[data.Id];
            foreach (var path in paths)
            {
                yield return (path, await _connection.Objects.GetAsync<BlobEntry>(data.Id));
            }
        }
    }

    private async Task<CommitContent> EnsureIndexAsync(CommitEntry commit)
    {
        var result = IndexExists(commit);
        if (result is not null)
        {
            return result;
        }
        var tree = await commit.GetRootTreeAsync();
        result = new CommitContent { Id = commit.Id, Blobs = [] };
        var collectionValues = await GetBlobIndexValuesAsync(tree, result);
        foreach (var (type, values) in collectionValues)
        {
            await (Task)typeof(GitIndexing).GetMethod(nameof(AddRange), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(type)
                .Invoke(this, [values])!;
        }
        _context.IndexedBlobs.UpsertRange(result.Blobs.Select(x => new IndexedBlob { Id = x.Value }));
        await _context.Commits.Upsert(result).NoUpdate().RunAsync();
        await _context.SaveChangesAsync();
        return result;
    }

    private CommitContent? IndexExists(CommitEntry commit) =>
        _context.Commits.Find(commit.Id);

    private async Task AddRange<TSet>(IEnumerable<BlobIndex> values) where TSet : class =>
        await _context.Set<TSet>().UpsertRange(values.Cast<TSet>()).NoUpdate().RunAsync();

    private async Task<Dictionary<Type, IList<BlobIndex>>> GetBlobIndexValuesAsync(TreeEntry tree, CommitContent commitBlobs)
    {
        var collectionValues = new Dictionary<Type, IList<BlobIndex>>();
        var blobs = await GetBlobEntriesAsync(tree).ToListAsync();
        var blobIds = blobs.Select(x => x.Blob.Id).ToHashSet();
        var existingIndexes = _context.IndexedBlobs.Where(x => blobIds.Contains(x.Id)).Select(x => x.Id).ToHashSet();
        foreach (var (blobTreeEntry, path) in blobs)
        {
            commitBlobs.Blobs[path] = blobTreeEntry.Id;
            if (existingIndexes.Contains(blobTreeEntry.Id))
            {
                continue;
            }
            await CreateBlobIndexEntries(collectionValues, blobTreeEntry, path);
        }

        return collectionValues;
    }

    private async Task CreateBlobIndexEntries(Dictionary<Type, IList<BlobIndex>> collectionValues, TreeEntryItem blobTreeEntry, string path)
    {
        var blob = await blobTreeEntry.GetEntryAsync<BlobEntry>();
        //if (blob.IsText)
        //{
        //    var document = new Document(
        //        blob.GetText()!,
        //        new Dictionary<string, object>()
        //        {
        //            ["path"] = path,
        //            ["id"] = blob.Id.ToString(),
        //        });
        //    await _context.VectorCollection.AddDocumentsAsync(_context.EmbeddingModel, [document]);
        //}
        var indexValues = _options.Value.IndexProviders
            .SelectMany(p => p(path, blobTreeEntry).ToEnumerable());
        foreach (var value in indexValues)
        {
            var values = collectionValues.TryGetValue(value.GetType(), out var list) ?
                list :
                collectionValues[value.GetType()] = [];
            values.Add(value);
        }
    }

    private static async IAsyncEnumerable<(TreeEntryItem Blob, string Path)> GetBlobEntriesAsync(TreeEntry root)
    {
        var stack = new Stack<(TreeEntry, string path)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            var (item, path) = stack.Pop();
            foreach (var child in item.Children)
            {
                var childPath = path.Length > 0 ? $"{path}/{child.Name}" : child.Name;
                if (child.Mode.EntryType == EntryType.Blob)
                {
                    yield return (child, childPath);
                }
                else if (child.Mode.EntryType == EntryType.Tree)
                {
                    var treeEntry = await child.GetEntryAsync<TreeEntry>();
                    stack.Push((treeEntry, childPath));
                }
            }
        }
    }

    ///// <summary>Finds similar blobs to the specified question.</summary>
    ///// <param name="question">The question to find similar blobs for.</param>
    ///// <param name="amount">The amount of similar blobs to find.</param>
    ///// <returns>An asynchronous enumerable of similar blobs.</returns>
    //public async IAsyncEnumerable<(string Path, BlobEntry Blob)> FindSimilarBlobs(string question, int amount = 5)
    //{
    //    var documents = await _context.VectorCollection.GetSimilarDocuments(_context.EmbeddingModel, question, amount);
    //    foreach (var document in documents)
    //    {
    //        if (document.Metadata.TryGetValue("path", out var pathAsObject) && pathAsObject is string path &&
    //            document.Metadata.TryGetValue("id", out var idAsObject) && idAsObject is string id)
    //        {
    //            var blob = await _connection.Objects.GetAsync<BlobEntry>(new HashId(id));
    //            yield return (path, blob);
    //        }
    //    }
    //}

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
