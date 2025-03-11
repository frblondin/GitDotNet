namespace GitDotNet.Indexing.Realm;

public partial class GitIndexing
{
    /// <summary>Indexes the Git repository for searching.</summary>
    public class Options
    {
        /// <summary>Gets the types of indexes to create.</summary>
        /// <param name="path">The path of the blob.</param>
        /// <param name="blob">The blob to index.</param>
        public delegate IAsyncEnumerable<BlobIndex> BlobIndexProvider(string path, TreeEntryItem blob);

        /// <summary>Gets the path of the index database.</summary>
        public required IList<Type> IndexTypes { get; init; }

        /// <summary>Gets the providers for the indexes.</summary>
        public required IList<BlobIndexProvider> IndexProviders { get; init; }
    }
}
