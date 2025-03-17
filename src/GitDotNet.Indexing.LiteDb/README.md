1. Define Realm objects extending BlobIndex which implements `IRealmObject` interface
2. Define mapping between blob data and Realm objects
3. Create an .NET class `Indexing` with following method
   - `EnsureIndexAsync(CommitEntry commit)`
   - `TResult Search<TIndex>(CommitEntry commit, Func<IQueryable<TIndex>, TResult> func)`