using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using GitDotNet.Writers;

namespace GitDotNet;

internal delegate ITransformationComposerInternal TransformationComposerFactory(IRepositoryInfo info);

[DebuggerDisplay("Count = {Count}")]
internal partial class TransformationComposer(IRepositoryInfo info,
    LockWriterFactory lockWriterFactory,
#if USE_FAST_IMPORT
    FastInsertWriterFactory fastInsertWriterFactory,
#endif
    PackWriterFactory packWriterFactory, IFileSystem fileSystem)
    : ITransformationComposerInternal
{
    private const int LooseCommitThreshold = 100;

    internal Dictionary<GitPath, (TransformationType ChangeType, Stream? Stream, FileMode? FileMode)> Changes { get; } = [];

    public int Count => Changes.Count;

    public ITransformationComposer AddOrUpdate(GitPath path, string data, FileMode? fileMode = null) =>
        AddOrUpdate(path, Encoding.UTF8.GetBytes(data), fileMode);

    public ITransformationComposer AddOrUpdate(GitPath path, byte[] data, FileMode? fileMode = null) =>
        AddOrUpdate(path, new MemoryStream(data), fileMode);

    public ITransformationComposer AddOrUpdate(GitPath path, Stream stream, FileMode? fileMode = null)
    {
        Changes[path] = (TransformationType.AddOrModified, stream, fileMode);
        return this;
    }

    public ITransformationComposer Remove(GitPath path)
    {
        Changes[path] = (TransformationType.Removed, default, null);
        return this;
    }

    public async Task<HashId> CommitAsync(string branch, CommitEntry commit, CommitOptions? options)
    {
        if (Changes.Count == 0 && !(options?.AllowEmpty ?? false))
        {
            throw new InvalidOperationException("No changes to commit. Use AddOrUpdate or Remove methods to make changes before committing.");
        }
#if USE_FAST_IMPORT
        return await new FastImportCommitWriter(info.Path, fastInsertWriterFactory, fileSystem).CommitAsync(this, branch, commit, options).ConfigureAwait(false);
#else
        var locker = lockWriterFactory(info);
        return await locker.DoAsync(async () =>
        {
            Branch.CheckFullReferenceName(branch);
            var parents = await commit.GetParentsAsync().ConfigureAwait(false);
            var firstParent = parents.FirstOrDefault();
            var rootTree = firstParent is not null ? await firstParent.GetRootTreeAsync().ConfigureAwait(false) : null;

            // Use loose objects for small changesets, pack files for larger ones
            var commitWriter = Changes.Count < LooseCommitThreshold ?
                (InternalCommitWriter)new LooseCommitWriter(this, info, fileSystem) :
                new PackCommitWriter(this, info, packWriterFactory, fileSystem);
            return await commitWriter.WriteAsync(rootTree, commit).ConfigureAwait(false);
        }).ConfigureAwait(false);
#endif
    }

    internal enum TransformationType
    {
        AddOrModified,
        Removed
    }
}

internal interface ITransformationComposerInternal : ITransformationComposer
{
    Task<HashId> CommitAsync(string branch, CommitEntry commit, CommitOptions? options);
}

/// <summary>Provides methods to compose transformations on a Git repository.</summary>
public interface ITransformationComposer
{
    /// <summary>Gets the number of changes in the composer.</summary>
    int Count { get; }

    /// <summary>Adds or updates a file in the repository with the specified path and data.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="data">The data to write to the file.</param>
    /// <param name="fileMode">The file mode of a Git tree entry item.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(GitPath path, string data, FileMode? fileMode = null);

    /// <summary>Adds or updates a file in the repository with the specified path and data.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="data">The data to write to the file.</param>
    /// <param name="fileMode">The file mode of a Git tree entry item.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(GitPath path, byte[] data, FileMode? fileMode = null);

    /// <summary>Adds or updates a file in the repository with the specified path and stream.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="stream">The stream containing the data to write to the file.</param>
    /// <param name="fileMode">The file mode of a Git tree entry item.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(GitPath path, Stream stream, FileMode? fileMode = null);

    /// <summary>Removes a file from the repository with the specified path.</summary>
    /// <remarks>Add '/' at the end of the path to indicate that folder and children should be removed.</remarks>
    /// <param name="path">The path of the file to remove.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer Remove(GitPath path);
}
